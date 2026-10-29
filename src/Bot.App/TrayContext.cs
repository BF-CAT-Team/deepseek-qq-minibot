using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Bot.App;

/// <summary>
/// 托盘宿主。对"不懂电脑的人"来说，这个图标就是全部交互：双击打开配置页，
/// 右键退出；关掉网页不影响机器人在后台跑。
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly BotHost _host;
    private readonly string _url;
    private readonly NotifyIcon _icon;

    public TrayContext(BotHost host, string url)
    {
        _host = host;
        _url = url;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置 / 看板", null, (_, _) => OpenWeb());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenFolder(AppPaths.LogDirectory));
        menu.Items.Add("打开数据目录", null, (_, _) => OpenFolder(AppPaths.Root));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "聊天机器人",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenWeb();
        _icon.BalloonTipTitle = "聊天机器人已启动";
        _icon.BalloonTipText = "双击图标打开设置页。";
        _icon.ShowBalloonTip(3000);
    }

    public void OpenWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.LogWarning(ex, "打开浏览器失败");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不弹窗打扰用户
        }
    }

    private void Exit()
    {
        _icon.Visible = false;
        try
        {
            _host.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出时不再抛
        }

        _icon.Dispose();
        ExitThread();
    }

    private static Icon CreateIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(76, 141, 255));
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Microsoft YaHei", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("聊", font, Brushes.White, 4f, 4f);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
