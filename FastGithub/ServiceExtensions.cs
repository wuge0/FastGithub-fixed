using FastGithub.DomainResolve;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PInvoke;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace FastGithub
{
    /// <summary>
    /// IHostBuilder扩展
    /// </summary>
    static class ServiceExtensions
    {
        /// <summary>
        /// 控制命令
        /// </summary>
        private enum Command
        {
            Start,
            Stop,
        }

        [SupportedOSPlatform("linux")]
        [DllImport("libc", SetLastError = true)]
        private static extern uint geteuid();

        /// <summary>
        /// 使用windows服务
        /// </summary>
        /// <param name="hostBuilder"></param> 
        /// <returns></returns>
        public static IHostBuilder UseWindowsService(this IHostBuilder hostBuilder)
        {
            return WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService(hostBuilder);
        }

        /// <summary>
        /// 运行主机
        /// </summary>
        /// <param name="app"></param>
        /// <param name="singleton"></param>
        public static void Run(this WebApplication app, bool singleton)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(FastGithub));
            if (UseCommand(logger) == false)
            {
                using var mutex = new Mutex(true, "Global\\FastGithub", out var firstInstance);
                if (singleton == false || firstInstance)
                {
                    app.Run();
                }
                else
                {
                    logger.LogWarning($"程序将自动关闭：系统已运行其它实例");
                }
            }
        }

        /// <summary>
        /// 使用命令
        /// </summary>
        /// <param name="logger"></param>
        /// <returns></returns>
        private static bool UseCommand(ILogger logger)
        {
            var args = Environment.GetCommandLineArgs();
            if (Enum.TryParse<Command>(args.Skip(1).FirstOrDefault(), true, out var cmd) == false)
            {
                return false;
            }

            var action = cmd == Command.Start ? "启动" : "停止";
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    UseCommandAtWindows(cmd);
                }
                else if (OperatingSystem.IsLinux())
                {
                    UseCommandAtLinux(cmd);
                }
                else
                {
                    return false;
                }
                logger.LogInformation($"服务{action}成功");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"服务{action}异常");
            }
            return true;
        }

        /// <summary>
        /// 应用控制指令
        /// </summary> 
        /// <param name="cmd"></param>
        [SupportedOSPlatform("windows")]
        private static void UseCommandAtWindows(Command cmd)
        {
            var binaryPath = Environment.GetCommandLineArgs().First();
            var serviceName = Path.GetFileNameWithoutExtension(binaryPath);
            var state = true;
            if (cmd == Command.Start)
            {
                state = ServiceInstallUtil.InstallAndStartService(serviceName, binaryPath);
            }
            else if (cmd == Command.Stop)
            {
                state = ServiceInstallUtil.StopAndDeleteService(serviceName);
            }

            if (state == false)
            {
                throw new Win32Exception();
            }
        }

        /// <summary>
        /// 应用控制指令
        /// </summary> 
        /// <param name="cmd"></param>
        [SupportedOSPlatform("linux")]
        private static void UseCommandAtLinux(Command cmd)
        {
            if (geteuid() != 0)
            {
                throw new UnauthorizedAccessException("无法操作服务：没有root权限");
            }

            var binaryPath = Path.GetFullPath(Environment.GetCommandLineArgs().First());
            var serviceName = Path.GetFileNameWithoutExtension(binaryPath);
            var serviceFilePath = $"/etc/systemd/system/{serviceName}.service";

            if (cmd == Command.Start)
            {
                var serviceBuilder = new StringBuilder()
                    .AppendLine("[Unit]")
                    .AppendLine($"Description={serviceName}")
                    .AppendLine()
                    .AppendLine("[Service]")
                    .AppendLine("Type=notify")
                    .AppendLine($"User={Environment.UserName}")
                    .AppendLine($"ExecStart={binaryPath}")
                    .AppendLine($"WorkingDirectory={Path.GetDirectoryName(binaryPath)}")
                    .AppendLine()
                    .AppendLine("[Install]")
                    .AppendLine("WantedBy=multi-user.target");
                File.WriteAllText(serviceFilePath, serviceBuilder.ToString());

                // SELinux场景可选，系统无chcon时不影响服务安装
                try
                {
                    RunExternalCommand("chcon", $"--type=bin_t {binaryPath}");
                }
                catch (Exception)
                {
                }
                RunExternalCommand("systemctl", "daemon-reload");
                RunExternalCommand("systemctl", $"start {serviceName}.service");
                RunExternalCommand("systemctl", $"enable {serviceName}.service");
            }
            else if (cmd == Command.Stop)
            {
                RunExternalCommand("systemctl", $"stop {serviceName}.service");
                RunExternalCommand("systemctl", $"disable {serviceName}.service");

                if (File.Exists(serviceFilePath))
                {
                    File.Delete(serviceFilePath);
                }
                RunExternalCommand("systemctl", "daemon-reload");
            }
        }

        /// <summary>
        /// 运行外部命令，带超时并释放进程句柄
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="arguments"></param>
        [SupportedOSPlatform("linux")]
        private static void RunExternalCommand(string fileName, string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException($"无法启动外部命令{fileName}");

            if (process.WaitForExit(TimeSpan.FromSeconds(30d)) == false)
            {
                process.Kill();
                throw new TimeoutException($"外部命令{fileName}执行超时");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"外部命令{fileName}执行失败，退出码{process.ExitCode}");
            }
        }
    }
}
