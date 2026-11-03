using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// app后台服务
    /// </summary>
    sealed class AppHostedService : BackgroundService
    {
        private readonly IHostApplicationLifetime lifetime;
        private readonly IOptions<AppOptions> appOptions;
        private readonly IOptions<FastGithubOptions> fastGithubOptions;
        private readonly ILogger<AppHostedService> logger;

        public AppHostedService(
            IHostApplicationLifetime lifetime,
            IOptions<AppOptions> appOptions,
            IOptions<FastGithubOptions> fastGithubOptions,
            ILogger<AppHostedService> logger)
        {
            this.lifetime = lifetime;
            this.appOptions = appOptions;
            this.fastGithubOptions = fastGithubOptions;
            this.logger = logger;
        }

        /// <summary>
        /// 启动完成
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            Version version;
            if (ProductionVersion.Current is not null)
            {
                version = ProductionVersion.Current.Version;
            }
            else
            {
                version = new Version(0, 0);
            }
            await base.StartAsync(cancellationToken);
            this.logger.LogInformation($"\n======[ {nameof(FastGithub)} 启动完成，当前版本为 V{version} ]======\n");
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1d), stoppingToken);
                await this.CheckFastGithubProxyAsync(stoppingToken);
                await this.WaitForParentProcessExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常停机，避免记录BackgroundService failed
            }
        }


        /// <summary>
        /// 检测fastgithub代理设置
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task CheckFastGithubProxyAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() == false)
            {
                try
                {
                    if (await this.UseFastGithubProxyAsync() == false)
                    {
                        var httpProxyPort = this.fastGithubOptions.Value.HttpProxyPort;
                        this.logger.LogWarning($"请设置系统自动代理为http://{IPAddress.Loopback}:{httpProxyPort}，或手动代理http/https为{IPAddress.Loopback}:{httpProxyPort}");
                    }
                }
                catch (Exception)
                {
                    this.logger.LogWarning("尝试获取代理信息失败");
                }
            }
        }


        /// <summary>
        /// 应用fastgithub代理
        /// </summary>
        /// <param name="proxyServer"></param>
        /// <param name="httpProxyPort"></param>
        /// <returns></returns>
        private async Task<bool> UseFastGithubProxyAsync()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return false;
            }

            var domain = this.fastGithubOptions.Value.DomainConfigs.Keys.FirstOrDefault();
            if (domain == null)
            {
                return true;
            }

            var destination = new Uri($"https://{domain.Replace('*', 'a')}");
            var proxyServer = systemProxy.GetProxy(destination);
            if (proxyServer == null)
            {
                return false;
            }

            var httpProxyPort = this.fastGithubOptions.Value.HttpProxyPort;
            if (proxyServer.Port != httpProxyPort)
            {
                return false;
            }

            if (IPAddress.TryParse(proxyServer.Host, out var address))
            {
                return IPAddress.IsLoopback(address);
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(proxyServer.Host);
                return addresses.Any(item => IPAddress.IsLoopback(item));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 等待父进程退出
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task WaitForParentProcessExitAsync(CancellationToken cancellationToken)
        {
            var parentId = this.appOptions.Value.ParentProcessId;
            if (parentId <= 0)
            {
                return;
            }

            try
            {
                // 异步等待父进程退出，避免同步阻塞线程池线程
                using var parentProcess = Process.GetProcessById(parentId);
                await parentProcess.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"获取进程{parentId}异常");
            }
            finally
            {
                this.logger.LogInformation($"正在主动关闭，因为父进程已退出");
                // 通过应用生命周期请求停止，避免在HostedService内调用host.StopAsync造成循环等待
                this.lifetime.StopApplication();
            }
        }

    }
}
