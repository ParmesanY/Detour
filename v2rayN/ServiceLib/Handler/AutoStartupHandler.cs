namespace ServiceLib.Handler;

public static class AutoStartupHandler
{
    private static readonly string _tag = "AutoStartupHandler";

    public static async Task<bool> UpdateTask(Config config)
    {
        if (Utils.IsWindows())
        {
            await ClearTaskWindows();

            if (config.GuiItem.AutoRun)
            {
                await SetTaskWindows();
            }
        }
        else if (Utils.IsLinux())
        {
            await ClearTaskLinux();

            if (config.GuiItem.AutoRun)
            {
                await SetTaskLinux();
            }
        }
        else if (Utils.IsMacOS())
        {
            await ClearTaskOSX();

            if (config.GuiItem.AutoRun)
            {
                await SetTaskOSX();
            }
        }

        return true;
    }

    #region Windows

    [SupportedOSPlatform("windows")]
    private static async Task ClearTaskWindows()
    {
        var autoRunName = GetAutoRunNameWindows();
        WindowsUtils.RegWriteValue(Global.AutoRunRegPath, autoRunName, "");
        if (Utils.IsAdministrator())
        {
            AutoStartTaskService(autoRunName, "", "");
        }
        // Entries of earlier builds (other folders, old name) would start a second client at logon.
        RemoveLegacyAutoRuns();

        await Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static async Task SetTaskWindows()
    {
        try
        {
            var autoRunName = GetAutoRunNameWindows();
            var exePath = Utils.GetExePath();
            if (Utils.IsAdministrator() && ElevatedAutoRunIsSafe(Path.GetDirectoryName(exePath)!))
            {
                AutoStartTaskService(autoRunName, exePath, "");
            }
            else
            {
                WindowsUtils.RegWriteValue(Global.AutoRunRegPath, autoRunName, exePath.AppendQuotes());
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// The logon task starts Detour elevated without asking. From a folder any program of the user can write to,
    /// that hands admin rights to whoever swaps Detour.exe or a DLL there. Then the plain Run entry is used instead:
    /// Detour starts without rights and asks for them through UAC (it does so itself when zapret is on).
    /// </summary>
    public static bool ElevatedAutoRunIsSafe(string appDir)
    {
        var writers = AppFolderAccess.NonAdminWriters(appDir);
        if (writers.Count == 0)
        {
            return true;
        }
        Logging.SaveLog($"{_tag}: {appDir} is writable by {string.Join(", ", writers)}: autorun without silent elevation");
        // Once: the next UAC prompt at logon would otherwise come out of nowhere.
        var marker = Utils.GetConfigPath("autorun-uac-noted");
        if (!File.Exists(marker))
        {
            NoticeManager.Instance.Enqueue(AppText.AutoRunNotElevated);
            try
            {
                File.WriteAllText(marker, "");
            }
            catch (IOException)
            {
            }
        }
        return false;
    }

    /// <summary>
    /// Auto Start via TaskService
    /// </summary>
    /// <param name="taskName"></param>
    /// <param name="fileName"></param>
    /// <param name="description"></param>
    /// <exception cref="ArgumentNullException"></exception>
    [SupportedOSPlatform("windows")]
    public static void AutoStartTaskService(string taskName, string fileName, string description)
    {
#if false
        if (taskName.IsNullOrEmpty())
        {
            return;
        }

        var logonUser = WindowsIdentity.GetCurrent().Name;
        using var taskService = new Microsoft.Win32.TaskScheduler.TaskService();
        var tasks = taskService.RootFolder.GetTasks(new Regex(taskName));
        if (fileName.IsNullOrEmpty())
        {
            foreach (var t in tasks)
            {
                taskService.RootFolder.DeleteTask(t.Name);
            }
            return;
        }

        var task = taskService.NewTask();
        task.RegistrationInfo.Description = description;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.RunOnlyIfIdle = false;
        task.Settings.IdleSettings.StopOnIdleEnd = false;
        task.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        task.Settings.Priority = ProcessPriorityClass.Normal;
        task.Triggers.Add(new Microsoft.Win32.TaskScheduler.LogonTrigger { UserId = logonUser });
        task.Principal.RunLevel = Microsoft.Win32.TaskScheduler.TaskRunLevel.Highest;
        task.Actions.Add(new Microsoft.Win32.TaskScheduler.ExecAction(fileName.AppendQuotes(), null, Path.GetDirectoryName(fileName)));

        taskService.RootFolder.RegisterTaskDefinition(taskName, task);
#endif
        if (string.IsNullOrEmpty(taskName))
        {
            return;
        }

        // Separate arguments: one "/Delete /TN name /F" string reaches schtasks as a single
        // invalid argument, so old tasks were never deleted.
        if (TaskExists(taskName))
        {
            RunSchtasks("/Delete", "/TN", taskName, "/F");
        }

        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        var workingDir = Path.GetDirectoryName(fileName) ?? string.Empty;

        var taskXml = EmbedUtils.GetEmbedText(Global.TaskXmlTemplateFileName);

        taskXml = taskXml
            .Replace("{{DESCRIPTION}}", System.Security.SecurityElement.Escape(description ?? ""))
            .Replace("{{EXEC_PATH}}", System.Security.SecurityElement.Escape(fileName ?? ""))
            .Replace("{{ARGUMENTS}}", string.Empty)
            .Replace("{{WORKING_DIRECTORY}}", System.Security.SecurityElement.Escape(workingDir ?? ""));

        var taskXmlFileName = Utils.GetTempPath($"{Utils.GetGuid(false)}.xml");

        File.WriteAllText(taskXmlFileName, taskXml);

        RunSchtasks(
            "/Create",
            "/TN", taskName,
            "/XML", taskXmlFileName,
            "/F"
        );
    }

    private static bool TaskExists(string taskName)
    {
        return RunSchtasksOutput(out _, "/Query", "/TN", taskName);
    }

    /// <summary>
    /// Removes autorun entries left by earlier builds of this client (named v2rayNAutoRun_* before the
    /// rename, with a hash of whatever folder that build lived in). Only entries whose exe sits next to
    /// a bin\zapret folder are ours; a separately installed upstream v2rayN is left alone.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RemoveLegacyAutoRuns()
    {
        try
        {
            var prefix = Global.LegacyAutoRunName + "_";
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Global.AutoRunRegPath, true);
            foreach (var name in run?.GetValueNames() ?? [])
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && IsOurOldBuild(run!.GetValue(name)?.ToString()))
                {
                    run.DeleteValue(name, false);
                    Logging.SaveLog($"{_tag}: removed old autorun value {name}");
                }
            }

            if (!Utils.IsAdministrator() || !RunSchtasksOutput(out var list, "/Query", "/FO", "CSV", "/NH"))
            {
                return;
            }
            var names = list.Split('\n')
                .Select(l => l.Split(',').FirstOrDefault()?.Trim().Trim('"').TrimStart('\\') ?? "")
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (RunSchtasksOutput(out var xml, "/Query", "/TN", name, "/XML")
                    && IsOurOldBuild(TaskCommand(xml)))
                {
                    RunSchtasks("/Delete", "/TN", name, "/F");
                    Logging.SaveLog($"{_tag}: removed old autorun task {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>Exe path from a task XML's &lt;Command&gt;.</summary>
    public static string? TaskCommand(string xml)
    {
        var m = Regex.Match(xml, "<Command>(.*?)</Command>", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"') : null;
    }

    /// <summary>The exe is a build of this client: it has bin\zapret next to it (upstream v2rayN never does).</summary>
    public static bool IsOurOldBuild(string? exePath)
    {
        exePath = exePath?.Trim().Trim('"');
        if (exePath.IsNullOrEmpty() || exePath!.Equals(Utils.GetExePath(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var dir = Path.GetDirectoryName(exePath);
        return dir != null && Directory.Exists(Path.Combine(dir, "bin", "zapret"));
    }

    private static bool RunSchtasksOutput(out string output, params string[] arguments)
    {
        output = string.Empty;
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }
        using var process = Process.Start(psi);
        if (process == null)
        {
            return false;
        }
        output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static bool RunSchtasks(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);

        if (process == null)
        {
            return false;
        }

        process.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!string.IsNullOrEmpty(output))
        {
            Logging.SaveLog($"RunSchtasks output: {output}");
        }
        if (!string.IsNullOrEmpty(error))
        {
            Logging.SaveLog($"RunSchtasks error: {error}");
        }

        return process.ExitCode == 0;
    }

    private static string GetAutoRunNameWindows(string prefix = Global.AutoRunName)
    {
        return $"{prefix}_{Utils.GetMd5(Utils.StartupPath())}";
    }

    #endregion Windows

    #region Linux

    [SupportedOSPlatform("linux")]
    private static async Task ClearTaskLinux()
    {
        try
        {
            File.Delete(GetHomePathLinux());
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        await Task.CompletedTask;
    }

    [SupportedOSPlatform("linux")]
    private static async Task SetTaskLinux()
    {
        try
        {
            var linuxConfig = EmbedUtils.GetEmbedText(Global.LinuxAutostartConfig);
            if (linuxConfig.IsNotEmpty())
            {
                linuxConfig = linuxConfig.Replace("$ExecPath$", Utils.GetExePath());
                Logging.SaveLog(linuxConfig);

                var homePath = GetHomePathLinux();
                await File.WriteAllTextAsync(homePath, linuxConfig);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string GetHomePathLinux()
    {
        var homePath = Path.Combine(Utils.GetHomePath(), ".config", "autostart", $"{Global.AppName}.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(homePath));
        return homePath;
    }

    #endregion Linux

    #region macOS

    [SupportedOSPlatform("macos")]
    private static async Task ClearTaskOSX()
    {
        try
        {
            var launchAgentPath = GetLaunchAgentPathMacOS();
            if (File.Exists(launchAgentPath))
            {
                var args = new[] { "-c", $"launchctl unload -w \"{launchAgentPath}\"" };
                await Utils.GetCliWrapOutput(Global.LinuxBash, args);

                File.Delete(launchAgentPath);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task SetTaskOSX()
    {
        try
        {
            var plistContent = GenerateLaunchAgentPlist();
            var launchAgentPath = GetLaunchAgentPathMacOS();
            await File.WriteAllTextAsync(launchAgentPath, plistContent);

            var args = new[] { "-c", $"launchctl load -w \"{launchAgentPath}\"" };
            await Utils.GetCliWrapOutput(Global.LinuxBash, args);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    [SupportedOSPlatform("macos")]
    private static string GetLaunchAgentPathMacOS()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launchAgentPath = Path.Combine(homePath, "Library", "LaunchAgents", $"{Global.AppName}-LaunchAgent.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(launchAgentPath));
        return launchAgentPath;
    }

    [SupportedOSPlatform("macos")]
    private static string GenerateLaunchAgentPlist()
    {
        var exePath = Utils.GetExePath();
        var appName = Path.GetFileNameWithoutExtension(exePath);
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{Global.AppName}-LaunchAgent</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/sh</string>
        <string>-c</string>
        <string>if ! pgrep -x ""{appName}"" > /dev/null; then ""{exePath}""; fi</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <false/>
</dict>
</plist>";
    }

    #endregion macOS
}
