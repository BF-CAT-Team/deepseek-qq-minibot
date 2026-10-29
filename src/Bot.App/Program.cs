using System.Diagnostics;
using System.Windows.Forms;

namespace Bot.App;

internal static class Program
{
    private const string MutexName = "Local\\ChatBot.SingleInstance.v12";

    [STAThread]
    private static void Main(string[] args)
    {
        // 生成《设置说明》后直接退出，不启动机器人。
        var dumpIndex = Array.FindIndex(args, a => string.Equals(a, "--dump-params", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0)
        {
            var target = args.Length > dumpIndex + 1 ? args[dumpIndex + 1] : "设置说明.md";
            ParamDoc.Write(target);
            return;
        }

        var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(a, "/headless", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew && !headless)
        {
            MessageBox.Show("聊天机器人已经在运行了，请查看系统托盘。", "聊天机器人",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var host = new BotHost();

        try
        {
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            if (headless)
            {
                Console.Error.WriteLine("启动失败：" + ex);
            }
            else
            {
                MessageBox.Show("启动失败：\n" + ex.Message + "\n\n日志目录：" + AppPaths.LogDirectory,
                    "聊天机器人", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return;
        }

        var url = $"http://{host.Settings.Server.Host}:{host.Settings.Server.Port}";

        if (headless)
        {
            // 无界面模式：给 Windows 服务 / Linux 服务器 / 自动化验证用。
            Console.WriteLine($"机器人已启动（headless）：{url}");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var tray = new TrayContext(host, url);

        if (host.Settings.Server.OpenBrowserOnStart)
        {
            tray.OpenWeb();
        }

        Application.Run(tray);

        try
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出路径不再打扰用户
        }
    }
}
