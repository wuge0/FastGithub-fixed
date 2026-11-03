using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept
{
    /// <summary>
    /// dns拦截后台服务
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class DnsInterceptHostedService : BackgroundService
    {
        private readonly IDnsInterceptor dnsInterceptor;
        private readonly IEnumerable<IDnsConflictSolver> conflictSolvers;
        private readonly ILogger<DnsInterceptHostedService> logger;
        private readonly IHostApplicationLifetime lifetime;

        /// <summary>
        /// dns拦截后台服务
        /// </summary>
        /// <param name="dnsInterceptor"></param>
        /// <param name="conflictSolvers"></param>
        /// <param name="logger"></param>
        /// <param name="lifetime"></param>
        public DnsInterceptHostedService(
            IDnsInterceptor dnsInterceptor,
            IEnumerable<IDnsConflictSolver> conflictSolvers,
            ILogger<DnsInterceptHostedService> logger,
            IHostApplicationLifetime lifetime)
        {
            this.dnsInterceptor = dnsInterceptor;
            this.conflictSolvers = conflictSolvers;
            this.logger = logger;
            this.lifetime = lifetime;
        }

        /// <summary>
        /// 启动时处理冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var solver in this.conflictSolvers)
            {
                // 单个solver失败不影响其余solver与启动
                try
                {
                    await solver.SolveAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, $"{solver.GetType().Name}处理冲突失败");
                }
            }
            await base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 停止时恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var solver in this.conflictSolvers)
            {
                // 单个solver恢复失败不影响其余solver
                try
                {
                    await solver.RestoreAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, $"{solver.GetType().Name}恢复冲突失败");
                }
            }
            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// dns后台
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnsInterceptor.InterceptAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 995)
            {
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "dns拦截器异常");
                this.lifetime.StopApplication();
            }
        }
    }
}
