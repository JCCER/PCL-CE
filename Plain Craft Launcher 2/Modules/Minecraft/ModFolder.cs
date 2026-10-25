using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;

namespace PCL;

public static class ModFolder
{
    /// <summary>
    ///     当前的 Minecraft 文件夹路径，以"\"结尾。
    /// </summary>
    public static string mcFolderSelected;

    /// <summary>
    ///     当前的 Minecraft 文件夹列表。
    /// </summary>
    public static List<McFolder> mcFolderList = new();

    public class McFolder // 必须是 Class，否则不是引用类型，在 ForEach 中不会得到刷新
    {
        public enum Types
        {
            Original,
            RenamedOriginal,
            Custom
        }

        /// <summary>
        ///     文件夹路径。
        ///     以 \ 结尾，例如 "D:\Game\MC\.minecraft\"。
        /// </summary>
        public string Location;

        /// <summary>
        ///     文件夹的显示名称。<br/>
        ///     若是内置文件夹，该字段保存本地化资源键（以 <c>$</c> 开头），显示时请使用 <see cref="DisplayName" />。
        /// </summary>
        public string Name;

        public Types type;

        /// <summary>
        ///     内置文件夹名称的资源键前缀，用于区分「资源键」与「用户自定义名称」。
        /// </summary>
        internal const string BuiltinNamePrefix = "$";

        /// <summary>
        ///     当前语言下的显示名称。内置文件夹会按当前语言实时翻译。
        /// </summary>
        public string DisplayName =>
            Name is not null && Name.Length > BuiltinNamePrefix.Length &&
            Name.StartsWith(BuiltinNamePrefix, StringComparison.Ordinal)
                ? Lang.Text(Name[BuiltinNamePrefix.Length..])
                : Name;

        /// <summary>
        ///     创建一个使用内置名称的文件夹。<br/>
        ///     写入 <see cref="Name" /> 的是资源键（带前缀），因此可以安全持久化。
        /// </summary>
        /// <param name="nameKey">内置名称的本地化资源键。</param>
        /// <param name="location">文件夹路径。</param>
        /// <param name="type">文件夹类型。</param>
        public static McFolder CreateBuiltin(string nameKey, string location, Types type)
        {
            return new McFolder
                { Name = BuiltinNamePrefix + nameKey, Location = location, type = type };
        }

        public override bool Equals(object obj)
        {
            if (obj is not McFolder)
                return false;
            var folder = (McFolder)obj;
            return (Name ?? "") == (folder.Name ?? "") && (Location ?? "") == (folder.Location ?? "") &&
                   type == folder.type;
        }

        public override string ToString()
        {
            return Location;
        }
    }

    /// <summary>
    ///     加载 Minecraft 文件夹列表。
    /// </summary>
    public static ModLoader.LoaderTask<int, int> mcFolderListLoader = new("Minecraft Folder List",
        _ => McFolderListLoadSub(), priority: ThreadPriority.AboveNormal);

    /// <summary>
    ///     旧版本把内置文件夹的译文直接写进了配置，升级后需要把这些译文迁移回资源键，
    ///     否则内置文件夹名称会永远停留在升级时的语言。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyBuiltinFolderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["当前文件夹"] = "Select.Folder.CurrentFolder",
            ["目前資料夾"] = "Select.Folder.CurrentFolder",
            ["Current folder"] = "Select.Folder.CurrentFolder",
            ["官方启动器文件夹"] = "Select.Folder.OfficialLauncherFolder",
            ["官方啟動器資料夾"] = "Select.Folder.OfficialLauncherFolder",
            ["Official launcher folder"] = "Select.Folder.OfficialLauncherFolder"
        };

    private static void McFolderListLoadSub()
    {
        try
        {
            // 初始化
            var cacheMcFolderList = new List<McFolder>();

            #region 读取自定义（Custom）文件夹，可能没有结果

            // 格式：TMZ 12>C://xxx/xx/|Test>D://xxx/xx/|名称>路径
            foreach (string folder in (IEnumerable)((dynamic)States.Game.Folders).Split("|"))
            {
                if (string.IsNullOrEmpty(folder))
                    continue;
                if (!folder.Contains(">") || !folder.EndsWithF(@"\"))
                {
                    HintService.Hint(Lang.Text("Select.Folder.Invalid", folder), HintType.Error);
                    continue;
                }

                var name = folder.Split(">")[0];
                var path = folder.Split(">")[1];
                try
                {
                    ModBase.CheckPermissionWithException(path);
                    cacheMcFolderList.Add(new McFolder { Name = name, Location = path, type = McFolder.Types.Custom });
                }
                catch (Exception ex)
                {
                    ModMain.MyMsgBox(
                        Lang.Text("Select.Folder.Invalid.WithDetail", path, ex.ToString()),
                        Lang.Text("Select.Folder.InvalidTitle"), isWarn: true);
                    ModBase.Log(ex, $"无法访问 Minecraft 文件夹 {path}");
                }
            }

            #endregion

            #region 读取默认（Original）文件夹，即当前、官启文件夹，可能没有结果

            var currentMcFolderList = new List<McFolder>();
            var originalMcFolderList = new List<McFolder>();
            // 扫描当前文件夹
            try
            {
                if (Directory.Exists(ModBase.exePath + @"versions\"))
                    originalMcFolderList.Add(McFolder.CreateBuiltin("Select.Folder.CurrentFolder",
                        ModBase.exePath, McFolder.Types.Original));
                foreach (var folder in new DirectoryInfo(ModBase.exePath).GetDirectories())
                    if (Directory.Exists(Path.Combine(folder.FullName, "versions")) || folder.Name == ".minecraft")
                    {
                        var newCurrentFolder = new McFolder
                            { Name = folder.Name, Location = folder.FullName + @"\", type = McFolder.Types.Original };
                        originalMcFolderList.Add(newCurrentFolder);
                        currentMcFolderList.Add(newCurrentFolder);
                    }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "扫描 PCL 所在文件夹中是否有 MC 文件夹失败");
            }

            // 扫描官启文件夹
            var mojangPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft") + @"\";
            if ((!currentMcFolderList.Any() || (mojangPath ?? "") != (currentMcFolderList[0].Location ?? "")) &&
                Directory.Exists(Path.Combine(mojangPath, "versions"))) // 当前文件夹不是官启文件夹
                // 具有权限且存在 versions 文件夹
                originalMcFolderList.Add(McFolder.CreateBuiltin(
                    "Select.Folder.OfficialLauncherFolder",
                    mojangPath, McFolder.Types.Original));

            ModBase.Log(cacheMcFolderList.Count + " 个自定义文件夹，" + originalMcFolderList.Count + " 个原始文件夹");

            foreach (var newOriginalFolder in originalMcFolderList)
            {
                var unAdded = true;
                foreach (var cacheFolder in cacheMcFolderList)
                    if ((cacheFolder.Location ?? "") == (newOriginalFolder.Location ?? ""))
                    {
                        var newOriginalName = newOriginalFolder.Name ?? "";
                        // 旧版本保存的是译文；仅当它确实是该内置文件夹的旧译名时才迁移，
                        // 以免覆盖用户手动改过的名称。
                        if (newOriginalName.StartsWith(
                                McFolder.BuiltinNamePrefix,
                                StringComparison.Ordinal) &&
                            LegacyBuiltinFolderNames.TryGetValue(
                                cacheFolder.Name ?? "",
                                out var legacyKey) &&
                            newOriginalName == McFolder.BuiltinNamePrefix + legacyKey)
                            cacheFolder.Name = newOriginalName;

                        cacheFolder.type = (cacheFolder.Name ?? "") != newOriginalName
                            ? McFolder.Types.RenamedOriginal
                            : McFolder.Types.Original;
                        unAdded = false;
                    }

                if (unAdded)
                    cacheMcFolderList.Add(newOriginalFolder); // 如果没有重命名，则添加当前文件夹
            }

            #endregion

            #region 读取自定义文件夹情况并写入设置

            // 将自定义文件夹情况同步到设置
            var config = new List<string>();
            foreach (var Folder in cacheMcFolderList)
                config.Add(Folder.Name + ">" + Folder.Location);
            if (!config.Any())
                config.Add(""); // 防止 0 元素 Join 返回 Nothing
            States.Game.Folders = config.Join("|");

            #endregion

            // 若没有可用文件夹，则创建 .minecraft
            if (!cacheMcFolderList.Any())
            {
                Directory.CreateDirectory(ModBase.exePath + @".minecraft\versions\");
                cacheMcFolderList.Add(McFolder.CreateBuiltin(
                    "Select.Folder.CurrentFolder",
                    ModBase.exePath + @".minecraft\", McFolder.Types.Original));
            }

            foreach (var Folder in cacheMcFolderList) McFolderLauncherProfilesJsonCreate(Folder.Location);
            if (Config.Debug.AddRandomDelay)
                Thread.Sleep(RandomUtils.NextInt(200, 2000));

            // 回设
            mcFolderList = cacheMcFolderList;
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Select.Folder.Error.Load"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Select.Folder.Error.Load"));
        }
    }

    /// <summary>
    ///     为 Minecraft 文件夹创建 launcher_profiles.json 文件。
    /// </summary>
    public static void McFolderLauncherProfilesJsonCreate(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, "launcher_profiles.json")))
                return;
            var now = DateTime.Now;
            var resultJson = @"{
    ""profiles"":  {
        ""PCL"": {
            ""icon"": ""Grass"",
            ""name"": ""PCL"",
            ""lastVersionId"": ""latest-release"",
            ""type"": ""latest-release"",
            ""lastUsed"": """ + now.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture) + "T" +
                             now.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture) + @".0000Z""
        }
    },
    ""selectedProfile"": ""PCL"",
    ""clientToken"": ""23323323323323323323323323323333""
}";
            ModBase.WriteFile(Path.Combine(folder, "launcher_profiles.json"), resultJson, encoding: Encoding.GetEncoding("GB18030"));
            ModBase.Log("[Minecraft] 已创建 launcher_profiles.json：" + folder);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "创建 launcher_profiles.json 失败（" + folder + "）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Folder.Error.OperationFailed"));
        }
    }
}
