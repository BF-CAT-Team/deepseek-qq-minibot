namespace Bot.App;

/// <summary>
/// 数据目录。优先绿色便携：exe 同级的 <c>data\</c>；不可写（例如装在 Program Files）时回落到
/// <c>%LOCALAPPDATA%\ChatBot</c>。用户拷走整个文件夹就等于搬走机器人。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(Resolve, isThreadSafe: true);

    public static string Root => RootLazy.Value;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string DbFile => Path.Combine(Root, "bot.db");

    public static string LogDirectory => Path.Combine(Root, "logs");

    public static bool IsPortable { get; private set; }

    private static string Resolve()
    {
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(portable);

            var probe = Path.Combine(portable, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            IsPortable = true;
            Directory.CreateDirectory(Path.Combine(portable, "logs"));
            return portable;
        }
        catch
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatBot");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(Path.Combine(local, "logs"));
            return local;
        }
    }
}
