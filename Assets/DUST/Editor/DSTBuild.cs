using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DUST.EditorTools
{
    // 本地輸出 Windows 版並打包成 zip，給 GitHub Release 用。
    // 輸出：Build/v{版本}/DUST/DUST.exe 與 Build/DUST-v{版本}-Windows.zip（Build/ 不進 git）
    public static class DSTBuild
    {
        const string MainScene = "Assets/DUST/DR2_Map/DR2_DesertRuins02.unity";

        public static string Version => "v" + PlayerSettings.bundleVersion;
        public static string ZipPath => $"Build/DUST-{Version}-Windows.zip";
        public static string ResultPath => $"Build/{Version}/build_result.txt";

        [MenuItem("DUST/Build/Windows Release")]
        public static void BuildWindowsRelease()
        {
            string dir = $"Build/{Version}/DUST";
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { MainScene },
                locationPathName = dir + "/DUST.exe",
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });

            var s = report.summary;
            string text = $"result={s.result}\nerrors={s.totalErrors}\nwarnings={s.totalWarnings}\nsizeMB={s.totalSize / 1048576}\ntime={s.totalTime}\n";
            foreach (var step in report.steps)
                foreach (var m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception) text += "ERR " + m.content + "\n";

            if (s.result == BuildResult.Succeeded)
            {
                // Burst / 備份用的除錯資料不能給玩家（*_DoNotShip、*_ButDontShipItWithYourGame）
                foreach (var d in Directory.GetDirectories(dir, "*DoNotShip*"))
                    Directory.Delete(d, true);
                foreach (var d in Directory.GetDirectories(dir, "*DontShip*"))
                    Directory.Delete(d, true);
                if (File.Exists(ZipPath)) File.Delete(ZipPath);
                ZipFile.CreateFromDirectory(dir, ZipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
                text += $"zip={Path.GetFullPath(ZipPath)}\nzipMB={new FileInfo(ZipPath).Length / 1048576}\n";
            }
            File.WriteAllText(ResultPath, text);
            Debug.Log("[DUST] Build " + s.result + "\n" + text);
        }
    }
}
