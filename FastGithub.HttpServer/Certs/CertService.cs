using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Text;

namespace FastGithub.HttpServer.Certs
{
    /// <summary>
    /// 证书服务
    /// </summary>
    sealed class CertService
    {
        private const string CACERT_PATH = "cacert";
        // 限制服务器证书缓存条目数，防止任意SNI导致缓存无界增长
        private readonly IMemoryCache serverCertCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });
        private readonly IEnumerable<ICaCertInstaller> certInstallers;
        private readonly ILogger<CertService> logger;
        private readonly object caCertLock = new();
        private X509Certificate2? caCert;
        private bool caCertTrusted;


        /// <summary>
        /// 获取证书文件路径
        /// </summary>
        public string CaCerFilePath { get; } = OperatingSystem.IsLinux() ? $"{CACERT_PATH}/fastgithub.crt" : $"{CACERT_PATH}/fastgithub.cer";

        /// <summary>
        /// 获取私钥文件路径
        /// </summary>
        public string CaKeyFilePath { get; } = $"{CACERT_PATH}/fastgithub.key";

        /// <summary>
        /// 证书服务
        /// </summary>
        /// <param name="certInstallers"></param>
        /// <param name="logger"></param>
        public CertService(
            IEnumerable<ICaCertInstaller> certInstallers,
            ILogger<CertService> logger)
        {
            this.certInstallers = certInstallers;
            this.logger = logger;
            Directory.CreateDirectory(CACERT_PATH);
        }

        /// <summary>
        /// 生成CA证书
        /// </summary> 
        public bool CreateCaCertIfNotExists()
        {
            if (File.Exists(this.CaCerFilePath) && File.Exists(this.CaKeyFilePath))
            {
                return false;
            }

            File.Delete(this.CaCerFilePath);
            File.Delete(this.CaKeyFilePath);

            var notBefore = DateTimeOffset.Now.AddDays(-1);
            var notAfter = DateTimeOffset.Now.AddYears(10);

            var subjectName = new X500DistinguishedName($"CN={nameof(FastGithub)}");
            this.caCert = CertGenerator.CreateCACertificate(subjectName, notBefore, notAfter);

            var privateKeyPem = this.caCert.GetRSAPrivateKey()?.ExportRSAPrivateKeyPem();
            File.WriteAllText(this.CaKeyFilePath, new string(privateKeyPem), Encoding.ASCII);
            RestrictCaKeyFilePermission(this.CaKeyFilePath);

            var certPem = this.caCert.ExportCertificatePem();
            File.WriteAllText(this.CaCerFilePath, new string(certPem), Encoding.ASCII);

            return true;
        }

        /// <summary>
        /// 收紧CA私钥文件的访问权限，仅允许当前用户读写
        /// </summary>
        /// <param name="filePath"></param>
        private static void RestrictCaKeyFilePermission(string filePath)
        {
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                else if (OperatingSystem.IsWindows())
                {
                    var security = new FileSecurity();
                    security.SetAccessRule(new FileSystemAccessRule(
                        Environment.UserName,
                        FileSystemRights.Read | FileSystemRights.Write,
                        AccessControlType.Allow));
                    new FileInfo(filePath).SetAccessControl(security);
                }
            }
            catch (Exception)
            {
                // 权限设置失败不阻断启动，但私钥文件仍保持默认权限
            }
        }

        /// <summary>
        /// 安装和信任CA证书
        /// </summary>
        public void InstallAndTrustCaCert()
        {
            if (this.caCertTrusted)
            {
                return;
            }

            var installer = this.certInstallers.FirstOrDefault(item => item.IsSupported());
            if (installer != null)
            {
                installer.Install(this.CaCerFilePath);
            }
            else
            {
                this.logger.LogWarning($"请根据你的系统平台手动安装和信任CA证书{this.CaCerFilePath}");
            }

            GitConfigSslverify(false);
            this.caCertTrusted = true;
        }

        /// <summary>
        /// 设置github.com的ssl验证
        /// </summary>
        /// <param name="value">是否验证</param>
        /// <returns></returns>
        public static bool GitConfigSslverify(bool value)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    // 仅关闭github.com域名的ssl验证，不影响其他仓库
                    Arguments = $"config --global http.https://github.com/.sslverify {value.ToString().ToLower()}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return process == null || process.WaitForExit(10_000);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 获取颁发给指定域名的证书
        /// </summary>
        /// <param name="domain"></param> 
        /// <returns></returns>
        public X509Certificate2 GetOrCreateServerCert(string? domain)
        {
            if (this.caCert == null)
            {
                lock (this.caCertLock)
                {
                    if (this.caCert == null)
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(File.ReadAllText(this.CaKeyFilePath));
                        using var certWithoutKey = new X509Certificate2(this.CaCerFilePath);
                        this.caCert = certWithoutKey.CopyWithPrivateKey(rsa);
                    }
                }
            }

            var key = $"{nameof(CertService)}:{domain}";
            var endCert = this.serverCertCache.GetOrCreate(key, GetOrCreateCert);
            return endCert!;

            // 生成域名的1年证书
            X509Certificate2 GetOrCreateCert(ICacheEntry entry)
            {
                entry.SetSize(1);
                var notBefore = DateTimeOffset.Now.AddDays(-1);
                var notAfter = DateTimeOffset.Now.AddYears(1);
                entry.SetAbsoluteExpiration(notAfter);

                var extraDomains = GetExtraDomains();

                var subjectName = new X500DistinguishedName($"CN={domain}");
                using var endCert = CertGenerator.CreateEndCertificate(this.caCert, subjectName, extraDomains, notBefore, notAfter);

                // 重新初始化证书，以兼容win平台不能使用内存证书
                return new X509Certificate2(endCert.Export(X509ContentType.Pfx));
            }
        }

        /// <summary>
        /// 获取域名
        /// </summary>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static IEnumerable<string> GetExtraDomains()
        {
            yield return Environment.MachineName;
            yield return IPAddress.Loopback.ToString();
            yield return IPAddress.IPv6Loopback.ToString();
        }
    }
}
