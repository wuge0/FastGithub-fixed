using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept.Dns
{
    /// <summary>
    /// host文件冲解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class HostsConflictSolver : IDnsConflictSolver
    {
        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<HostsConflictSolver> logger;

        /// <summary>
        /// host文件冲解决者
        /// </summary>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public HostsConflictSolver(
            FastGithubConfig fastGithubConfig,
            ILogger<HostsConflictSolver> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task SolveAsync(CancellationToken cancellationToken)
        {
            var hostsPath = GetHostsPath();
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            Encoding hostsEncoding;
            var hasConflicting = false;
            var hostsBuilder = new StringBuilder();
            using (var fileStream = new FileStream(hostsPath, FileMode.Open, FileAccess.Read))
            {
                using var streamReader = new StreamReader(fileStream);
                while (streamReader.EndOfStream == false)
                {
                    var line = await streamReader.ReadLineAsync(cancellationToken);
                    if (this.IsConflictingLine(line))
                    {
                        hasConflicting = true;
                        hostsBuilder.AppendLine($"# {line}");
                    }
                    else
                    {
                        hostsBuilder.AppendLine(line);
                    }
                }
                hostsEncoding = streamReader.CurrentEncoding;
            }


            if (hasConflicting == true)
            {
                try
                {
                    // 修改前备份原始hosts文件，供RestoreAsync恢复
                    var backupPath = GetBackupPath(hostsPath);
                    if (File.Exists(backupPath) == false)
                    {
                        File.Copy(hostsPath, backupPath);
                    }

                    await WriteHostsFileAsync(hostsPath, hostsBuilder.ToString(), hostsEncoding, cancellationToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"无法解决hosts文件冲突：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 恢复冲突：从备份恢复原始hosts文件
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RestoreAsync(CancellationToken cancellationToken)
        {
            var hostsPath = GetHostsPath();
            var backupPath = GetBackupPath(hostsPath);
            if (File.Exists(backupPath) == false)
            {
                return;
            }

            try
            {
                var content = await File.ReadAllTextAsync(backupPath, cancellationToken);
                await WriteHostsFileAsync(hostsPath, content, Encoding.UTF8, cancellationToken);
                File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"无法恢复hosts文件：{ex.Message}");
            }
        }

        /// <summary>
        /// 获取hosts文件路径
        /// </summary>
        /// <returns></returns>
        private static string GetHostsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");
        }

        /// <summary>
        /// 获取hosts备份文件路径
        /// </summary>
        /// <param name="hostsPath"></param>
        /// <returns></returns>
        private static string GetBackupPath(string hostsPath)
        {
            return hostsPath + ".fastgithub.bak";
        }

        /// <summary>
        /// 原子写hosts文件，避免进程中断导致文件损坏
        /// </summary>
        /// <param name="hostsPath"></param>
        /// <param name="content"></param>
        /// <param name="encoding"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task WriteHostsFileAsync(string hostsPath, string content, Encoding encoding, CancellationToken cancellationToken)
        {
            var tempPath = hostsPath + ".fastgithub.tmp";
            await File.WriteAllTextAsync(tempPath, content, encoding, cancellationToken);
            try
            {
                File.Replace(tempPath, hostsPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(tempPath, hostsPath, true);
                File.Delete(tempPath);
            }
        }

        /// <summary>
        /// 是否为冲突的行
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private bool IsConflictingLine(string? line)
        {
            if (line == null || line.TrimStart().StartsWith("#"))
            {
                return false;
            }

            var items = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                return false;
            }

            var domain = items[1];
            return this.fastGithubConfig.IsMatch(domain);
        }
    }
}
