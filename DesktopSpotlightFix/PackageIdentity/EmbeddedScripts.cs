namespace DesktopSpotlightFix.PackageIdentity;

/// <summary>
/// 在 CBS 包身份里执行的 Windows PowerShell 5.1 片段。
/// 入口点始终是系统任务名，helper 不会把自己登记成 BackgroundTask。
/// </summary>
public static class EmbeddedScripts
{
    public const string EnumTasks = """
        try {
          $ai = [Windows.ApplicationModel.AppInfo,Windows.ApplicationModel,ContentType=WindowsRuntime]::Current
          W ("AUMID=" + $ai.AppUserModelId)
        } catch { W 'AUMID=?' }
        $rt = [Windows.ApplicationModel.Background.BackgroundTaskRegistration,Windows.ApplicationModel,ContentType=WindowsRuntime]
        foreach ($kv in $rt::AllTasks) { W ("TASK=" + $kv.Value.Name) }
        try {
          $st = [Windows.ApplicationModel.Background.BackgroundExecutionManager,Windows.ApplicationModel,ContentType=WindowsRuntime]::GetAccessStatus()
          W ("ACCESS=" + $st)
        } catch { W 'ACCESS=?' }
        """;

    public const string RegisterDesktop = """
        $bld = [Windows.ApplicationModel.Background.BackgroundTaskBuilder,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $tt  = [Windows.ApplicationModel.Background.TimeTrigger,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $st  = [Windows.ApplicationModel.Background.SystemTrigger,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $ste = [Windows.ApplicationModel.Background.SystemTriggerType,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $rt  = [Windows.ApplicationModel.Background.BackgroundTaskRegistration,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $have = @{}
        foreach ($kv in $rt::AllTasks) { $have[$kv.Value.Name] = $true }
        $plan = @(
          @{ n = 'DesktopSpotlight.BackgroundTask.UpdateTimer'; kind = 'time'; min = 15 },
          @{ n = 'DesktopSpotlight.BackgroundTask.RegistrationStatusCheck'; kind = 'sys'; st = 'SessionConnected' },
          @{ n = 'DesktopSpotlight.BackgroundTask.Maintenance'; kind = 'sys'; st = 'SessionConnected' },
          @{ n = 'DesktopSpotlight.BackgroundTask.OnlineIdChange'; kind = 'sys'; st = 'OnlineIdConnectedStateChange' }
        )
        foreach ($p in $plan) {
          if ($have.ContainsKey($p.n)) { W ("SKIP=" + $p.n); continue }
          try {
            $b = [Activator]::CreateInstance($bld)
            $b.Name = $p.n
            $b.TaskEntryPoint = $p.n
            if ($p.kind -eq 'time') { $trig = [Activator]::CreateInstance($tt, @([uint32]$p.min, $false)) }
            else { $trig = [Activator]::CreateInstance($st, @([Enum]::Parse($ste, $p.st), $false)) }
            $b.SetTrigger($trig)
            $null = $b.Register()
            W ("OK=" + $p.n)
          } catch { W ("FAIL=" + $p.n + " " + $_.Exception.Message) }
        }
        """;

    public const string RegisterIris = """
        $bld = [Windows.ApplicationModel.Background.BackgroundTaskBuilder,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $tt  = [Windows.ApplicationModel.Background.TimeTrigger,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $st  = [Windows.ApplicationModel.Background.SystemTrigger,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $ste = [Windows.ApplicationModel.Background.SystemTriggerType,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $rt  = [Windows.ApplicationModel.Background.BackgroundTaskRegistration,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $have = @{}
        foreach ($kv in $rt::AllTasks) { $have[$kv.Value.Name] = $true }
        $plan = @(
          @{ n = 'IrisService.BackgroundTask.UpdateTimer'; kind = 'time'; min = 15 },
          @{ n = 'IrisService.BackgroundTask.Maintenance'; kind = 'sys'; st = 'SessionConnected' },
          @{ n = 'IrisService.BackgroundTask.OnlineIdChange'; kind = 'sys'; st = 'OnlineIdConnectedStateChange' }
        )
        foreach ($p in $plan) {
          if ($have.ContainsKey($p.n)) { W ("SKIP=" + $p.n); continue }
          try {
            $b = [Activator]::CreateInstance($bld)
            $b.Name = $p.n
            $b.TaskEntryPoint = $p.n
            if ($p.kind -eq 'time') { $trig = [Activator]::CreateInstance($tt, @([uint32]$p.min, $false)) }
            else { $trig = [Activator]::CreateInstance($st, @([Enum]::Parse($ste, $p.st), $false)) }
            $b.SetTrigger($trig)
            $null = $b.Register()
            W ("OK=" + $p.n)
          } catch { W ("FAIL=" + $p.n + " " + $_.Exception.Message) }
        }
        """;

    public const string ScheduleOneShot = """
        $bld = [Windows.ApplicationModel.Background.BackgroundTaskBuilder,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $tt  = [Windows.ApplicationModel.Background.TimeTrigger,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $rt  = [Windows.ApplicationModel.Background.BackgroundTaskRegistration,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $name = 'DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot'
        $pending = $false
        foreach ($kv in $rt::AllTasks) { if ($kv.Value.Name -eq $name) { $pending = $true } }
        if ($pending) { W 'SCHEDULED=EXISTING' }
        else {
          try {
            $b = [Activator]::CreateInstance($bld)
            $b.Name = $name
            $b.TaskEntryPoint = 'DesktopSpotlight.BackgroundTask.UpdateTimer'
            $trig = [Activator]::CreateInstance($tt, @([uint32]15, $true))
            $b.SetTrigger($trig)
            $reg = $b.Register()
            W ("SCHEDULED=" + $reg.TaskId)
          } catch {
            W ("FAIL=" + $_.Exception.Message)
          }
        }
        """;

    public const string RemoveOneShot = """
        $rt = [Windows.ApplicationModel.Background.BackgroundTaskRegistration,Windows.ApplicationModel,ContentType=WindowsRuntime]
        $name = 'DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot'
        $targets = @()
        foreach ($kv in $rt::AllTasks) {
          if ($kv.Value.Name -eq $name) { $targets += $kv.Value }
        }
        if ($targets.Count -eq 0) { W 'REMOVED=0' }
        else {
          foreach ($t in $targets) {
            if ($t.Name -ne $name) { W 'REFUSED=name-mismatch'; continue }
            try {
              $t.Unregister($true)
              W 'REMOVED=1'
            } catch { W ("FAIL=" + $_.Exception.Message) }
          }
        }
        """;
}
