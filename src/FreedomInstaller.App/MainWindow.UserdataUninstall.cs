using System;
using System.IO;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

/// <summary>
/// 卸载向导控制器层：字段、模式谓词、门槛、作废与真实只读准备。
/// <para>
/// <c>uninstall</c> 复用共享设备准备页，但跳过内部第 1 步（全新备份页）：
/// 第 0 步真实安卓自动检查（或历史 9008 只读核对），第 2 步选择首次安装前保存的原始 GPT 备份，
/// 由软件校验文件与设备一致后自动另存当前状态回滚快照，并真实准备恢复方案；
/// 内部第 3 步展示恢复方案与风险，设备写入由执行层负责。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>卸载后端；null 表示准备尚未完成。</summary>
    private EdlUninstallBackend? _userdataUninstallBackend;

    /// <summary>用户是否勾选“恢复原厂安卓引导”；默认不勾选，保持项目菜单默认安卓行为。</summary>
    private bool _userdataUninstallRestoreStockBoot;
    private bool _userdataUninstallReclaimBackup = true;

    /// <summary>已提交的原厂引导恢复方案；仅在勾选恢复引导且准备成功时非 null。</summary>
    private BootMaintenancePrepared? _userdataUninstallStockPlan;

    /// <summary>勾选恢复引导执行后的恢复结果；未勾选或未执行时为 null。</summary>
    private BootMaintenanceResult? _userdataUninstallStockRestoreResult;

    /// <summary>程序化同步复选框时抑制 Checked/Unchecked 处理器递归触发。</summary>
    private bool _userdataUninstallStockCheckboxSuppress;

    /// <summary>真实打开的原始预安装 GPT 备份；只读，保持到方案作废或执行。</summary>
    private UninstallOriginalGptBackup? _userdataUninstallOriginal;

    /// <summary>真实准备完成的默认安卓引导方案；保持到方案作废或执行。</summary>
    private CurrentBootMenuPlan? _userdataUninstallMenu;
    private bool HasPreparedUninstallBootPlan => _userdataUninstallRestoreStockBoot
        ? _userdataUninstallStockPlan is not null : _userdataUninstallMenu is not null;

    /// <summary>本次卸载准备操作是否进行中；与维护同名字段同构，只参与保护判定。</summary>
    private bool _userdataUninstallOperationActive;

    /// <summary>最近一次真实准备失败的原始诊断；成功或作废时清空。</summary>
    private string? _userdataUninstallError;

    /// <summary>卸载模式判断。</summary>
    private bool IsModernUserdataUninstallMode =>
        string.Equals(CurrentModeTag(), "uninstall", StringComparison.Ordinal);

    /// <summary>
    /// 准备进行中或执行状态未解决时，保护卸载现场。
    /// </summary>
    private bool ProtectedModernUserdataUninstallState =>
        _userdataUninstallOperationActive || HasUnresolvedModernUserdataUninstallState;

    /// <summary>
    /// 共享四步的内部前进门槛（卸载不显示内部第 1 步）：第 0 步空闲即可；
    /// 第 2 步只要求真实设备入口前提（安卓检查通过或已验证的 9008 会话），
    /// 原始备份校验与当前状态快照由准备流程自己完成；第 3 步只认真实工厂结果。
    /// </summary>
    private bool CanEnterModernUserdataUninstallStep(int step)
    {
        if (!_ready || !IsModernUserdataUninstallMode || _busy || _userdataUninstallOperationActive)
        {
            return false;
        }

        // 已完成或未解决：只允许停留在当前第 3 步确认现场，不再返回任何输入页。
        if ((_userdataUninstallCompleted || HasUnresolvedModernUserdataUninstallState)
            && step != _internalInstallStep)
        {
            return false;
        }

        return step switch
        {
            0 => true,
            // 卸载不使用内部全新备份页：该步始终不可进入。
            1 => false,
            2 => HasBootMaintenanceBackupPrerequisite,
            3 => _userdataUninstallBackend is not null
                && HasPreparedUninstallBootPlan
                && _userdataUninstallOriginal is not null,
            _ => false,
        };
    }

    /// <summary>卸载向导正向导航：跳过内部第 1 步，保持 0 → 2 → 3 与显示序号 01/02/03。</summary>
    private static int NextModernUserdataUninstallStep(int step) => step == 0 ? 2 : step == 2 ? 3 : step;

    /// <summary>卸载向导反向导航：与正向对称跳过内部第 1 步。</summary>
    private static int PreviousModernUserdataUninstallStep(int step) => step == 3 ? 2 : step == 2 ? 0 : step;

    // ---------- 作废与复位 ----------

    /// <summary>
    /// 作废当前卸载准备：先释放后端，再释放方案，最后释放原始备份（只释放自己的租约/候选 GUID）。
    /// 绝不释放 fresh 备份、客户端，也绝不删除用户备份文件。
    /// </summary>
    private void InvalidateModernUserdataUninstallPreparation()
    {
        if (ProtectedModernUserdataUninstallState)
        {
            return;
        }

        // 受保护检查通过、确认要释放自有对象时，一并清掉存储的退出状态。
        _userdataUninstallExitStatus = null;

        if (_userdataUninstallBackend is { } backend)
        {
            backend.Dispose();
            _userdataUninstallBackend = null;
        }

        if (_userdataUninstallMenu is { } menu)
        {
            menu.Dispose();
            _userdataUninstallMenu = null;
        }

        if (_userdataUninstallOriginal is { } original)
        {
            original.Dispose();
            _userdataUninstallOriginal = null;
        }

        _userdataUninstallStockPlan = null;
        _userdataUninstallStockRestoreResult = null;

        if (_ready)
        {
            BootMaintenanceRiskCheck.IsChecked = false;
        }

        _userdataUninstallError = null;

        // 未完成（受保护检查已证明无未解决现场）时：清掉真实执行标记，
        // 让真实预检失败可以返回、重新准备并执行一次新的真实后端；复位痕迹在仍有会话时保留。
        if (!_userdataUninstallCompleted)
        {
            _userdataUninstallLastResult = null;
            _userdataUninstallExecuteAttempted = false;
            _userdataUninstallExecutionUnknown = false;
            _userdataUninstallDisplayStage = null;
        }
    }

    /// <summary>
    /// 安全复位卸载向导到第 0 步：只作废准备，保留已选共享路径（历史目录仍可服务其他模式）。
    /// </summary>
    private void SafeResetModernUserdataUninstallState()
    {
        if (ProtectedModernUserdataUninstallState)
        {
            return;
        }

        // 只在没有真实 9008 会话时清空执行状态；保留已选原始备份声明（路径仍可服务其他模式）。
        // 同一保护范围内：未被现场锁定的用户选择也一并复位；存在真实会话时保留用户选择。
        if (!HasInternalInstallLiveEdlState)
        {
            _userdataUninstallRestoreStockBoot = false;
            _userdataUninstallReclaimBackup = true;
            _userdataUninstallStockPlan = null;
            _userdataUninstallStockRestoreResult = null;
            SyncUserdataUninstallRestoreStockCheckboxes();

            _userdataUninstallLastResult = null;
            _userdataUninstallExecuteAttempted = false;
            _userdataUninstallExecutionUnknown = false;
            _userdataUninstallResetAttempted = false;
            _userdataUninstallResetAcked = false;
            _userdataUninstallCompleted = false;
            _userdataUninstallCloseApproved = false;
            _userdataUninstallCompletionToastShown = false;
            _userdataUninstallDisplayStage = null;
            _userdataUninstallExitStatus = null;
        }

        InvalidateModernUserdataUninstallPreparation();
        _internalInstallStep = 0;

        // 复位后只作废旧的真实检查 UI 证明；没有活动的安装 EDL 现场时才允许，绝不发设备 I/O，
        // 也绝不从这里递归触发安全复位或伪造就绪；未知阶段原样保留。
        if (!HasInternalInstallLiveEdlState)
        {
            ResetInternalInstallCheckState(false);
        }
    }

    // ---------- 真实只读准备 ----------

    /// <summary>
    /// 来源页「下一步」的真实只读准备：先按所选原始 GPT 备份推导安全工作父目录，
    /// 需要时等待与「开始备份」完全同一条 Android→9008→当前状态快照流水线，
    /// 再打开原始 GPT 备份 → 读真实当前槽位 → 确认资产 → 准备默认安卓引导方案 → 走标准卸载工厂。
    /// 只有工厂真实返回且捕获未变、仍在卸载模式、仍在同一步骤时才提交字段；
    /// 失败时保留现场并写真实诊断，绝不禁用保护锁。
    /// </summary>
    private async System.Threading.Tasks.Task PrepareModernUserdataUninstallConfirmationAsync()
    {
        if (!_ready
            || !IsModernUserdataUninstallMode
            || _internalInstallStep != 2
            || _busy
            || _userdataUninstallOperationActive
            || ProtectedModernUserdataUninstallState
            || !HasBootMaintenanceBackupPrerequisite
            || _runner is null)
        {
            return;
        }

        var runner = _runner;
        var capturedStep = _internalInstallStep;
        var capturedRestoreStock = _userdataUninstallRestoreStockBoot;
        var capturedReclaimBackup = _userdataUninstallReclaimBackup;
        var selectedOriginal = _bootMaintenanceOriginalDirectory;
        var workParent = _internalInstallBackupParent;
        if (string.IsNullOrWhiteSpace(workParent) || !Directory.Exists(workParent))
        {
            if (!string.IsNullOrWhiteSpace(selectedOriginal) && Directory.Exists(selectedOriginal))
                workParent = Directory.GetParent(Path.GetFullPath(selectedOriginal))?.FullName;
            if (string.IsNullOrWhiteSpace(workParent) || !Directory.Exists(workParent))
                workParent = DefaultRecoveryOutputDirectory();
            if (string.IsNullOrWhiteSpace(workParent)) return;
        }
        // 先作废上一次未写入的只读准备；再写入推导出的真实工作父目录（路径不同才写）。
        InvalidateModernUserdataUninstallPreparation();
        if (!string.Equals(_internalInstallBackupParent, workParent, StringComparison.Ordinal))
        {
            ApplyInternalInstallBackupParent(workParent);
        }

        // 尚无真实当前状态快照时，先等待与「开始备份」完全同一条流水线（不嵌套任何忙碌范围）。
        if (_internalInstallBackup is null)
        {
            SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.PreparingSnapshot"));
            await BackupInternalInstallCoreAsync();

            // 快照生成后无条件复核全部前提：任何一项变化都不提交，停在来源页更清晰。
            if (!IsModernUserdataUninstallMode
                || _closed
                || _internalInstallStep != capturedStep
                || !string.Equals(_bootMaintenanceOriginalDirectory, selectedOriginal, StringComparison.Ordinal)
                || _userdataUninstallRestoreStockBoot != capturedRestoreStock
                || _userdataUninstallReclaimBackup != capturedReclaimBackup)
            {
                return;
            }
        }

        // 无条件复核全部前提（不再仅在 if 内部）：任何一项失败都停在来源页，不进入确认。
        if (!IsModernUserdataUninstallMode
            || _closed
            || _internalInstallStep != capturedStep
            || !string.Equals(_bootMaintenanceOriginalDirectory, selectedOriginal, StringComparison.Ordinal)
            || _userdataUninstallRestoreStockBoot != capturedRestoreStock
            || _userdataUninstallReclaimBackup != capturedReclaimBackup
            || !HasBootMaintenanceDeviceProof
            || _internalInstallEdlClient is null
            || _internalInstallEdlTools is null
            || _internalInstallEdlEntry is null
            || _internalInstallBackup is null
            || _internalInstallBackupParent is null
            || Directory.Exists(_internalInstallBackupParent) == false)
        {
            return;
        }

        // 精确捕获：只有这些值在工厂返回时仍然一致才允许提交。
        var client = _internalInstallEdlClient!;
        var tools = _internalInstallEdlTools!;
        var entry = _internalInstallEdlEntry!;
        var fresh = _internalInstallBackup!;
        var parent = _internalInstallBackupParent!;

        _userdataUninstallOperationActive = true;

        try
        {
            await RunBusyAsync(async token =>
            {
                UninstallOriginalGptBackup? original = null;
                CurrentBootSlotFacts? facts = null;
                EdlRawBootTools? actualTools = null;
                CurrentBootMenuPlan? menu = null;
                EdlUninstallBackend? backend = null;
                BootMaintenancePrepared? stockPrepared = null;
                var committed = false;
                var acceptProgress = true;
                try
                {
                    SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.OpeningOriginal"));

                    var recoverySource = await GetBoardFirstRecoverySourceAsync(fresh, parent, token);
                    var originalDirectory = recoverySource.DirectoryPath;
                    // 1) 只读打开原始预安装 GPT 备份。
                    original = await InternalInstallBackup.OpenOriginalGptForUninstallAsync(
                        client,
                        originalDirectory,
                        progress: null,
                        token);

                    // 2) 从全新备份的 misc 分区读真实当前槽位。
                    SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.ReadingSlot"));
                    if (!fresh.Files.TryGetValue("misc", out var misc)
                        || misc is not EdlCapturedRegion miscRegion)
                    {
                        throw new InvalidOperationException(T("Dynamic.UserdataUninstall.Status.PrepareFailed"));
                    }

                    facts = await CurrentBootSlot.ReadAsync(fresh, token);

                    // 3b) 勾选恢复原厂安卓引导时：在提交卸载后端之前，只读地准备一份原厂引导恢复方案，
                    //     立即释放校验后端（释放输入文件租约），仅保留不可变的 Prepared。
                    if (capturedRestoreStock)
                    {
                        SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.PreparingPlan"));
                        var history = await BootMaintenanceBackend.ReadHistoricalIdentityAsync(
                            fresh.DirectoryPath,
                            token);
                        using var stockValidation = await BootMaintenanceBackend.CreatePreparedFromEdlAsync(
                            MaintenanceMode.RestoreStock,
                            history,
                            client,
                            fresh,
                            new BootMaintenanceSources(OriginalBackupDirectory: originalDirectory),
                            runner,
                            TemporaryWorkRoot,
                            progress: null,
                            token);
                        stockPrepared = stockValidation.Prepared;
                    }

                    SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.PreparingPlan"));
                    var assets = UninstallGptAssets.FromVerifiedBackup(original);
                    var backendProgress = new Progress<EdlUninstallProgress>(report =>
                    {
                        if (acceptProgress) RenderModernUserdataUninstallPrepareProgress(report);
                    });
                    if (capturedRestoreStock)
                    {
                        backend = await EdlUninstallBackend.CreatePreparedForStockRestoreAsync(
                            client, tools, entry, assets, fresh, stockPrepared!, TemporaryWorkRoot,
                            runner, backendProgress, token, capturedReclaimBackup);
                    }
                    else
                    {
                        await EnsureInternalInstallAssetsAsync(parent, fresh, token);
                        actualTools = _internalInstallNativeTools
                            ?? throw new InvalidOperationException("Required boot tools were not prepared.");
                        menu = await CurrentBootMenuPlanner.PrepareAndroidDefaultAsync(
                            fresh, facts.CurrentSlot,
                            HasCheckedInternalInstallDevice
                                ? _internalInstallDeviceCheck!.Android!.Identity.BootloaderState
                                : BootloaderState.Unknown,
                            actualTools, runner, TemporaryWorkRoot, token);
                        backend = await EdlUninstallBackend.CreatePreparedAsync(
                            client, tools, entry, assets, fresh, menu, facts.CurrentSlot,
                            TemporaryWorkRoot, runner, backendProgress, token, capturedReclaimBackup);
                    }

                    // 全部真实返回后再核对捕获；一致才提交三个自有字段。
                    if (!IsModernUserdataUninstallMode
                        || _closed
                        || !ReferenceEquals(_internalInstallEdlClient, client)
                        || !ReferenceEquals(_internalInstallEdlTools, tools)
                        || !ReferenceEquals(_internalInstallEdlEntry, entry)
                        || !ReferenceEquals(_internalInstallBackup, fresh)
                        || !string.Equals(_internalInstallBackupParent, parent, StringComparison.Ordinal)
                        || !string.Equals(_bootMaintenanceOriginalDirectory, selectedOriginal, StringComparison.Ordinal)
                        || _userdataUninstallRestoreStockBoot != capturedRestoreStock
                        || _userdataUninstallReclaimBackup != capturedReclaimBackup
                        || _internalInstallStep != capturedStep
                        || _userdataUninstallBackend is not null
                        || _userdataUninstallMenu is not null
                        || _userdataUninstallOriginal is not null)
                    {
                        // 捕获已变：只返回，由 finally 统一释放三个自有局部对象。
                        return;
                    }

                    _userdataUninstallBackend = backend;
                    _userdataUninstallMenu = menu;
                    _userdataUninstallOriginal = original;
                    _userdataUninstallStockPlan = capturedRestoreStock ? stockPrepared : null;
                    _userdataUninstallStockRestoreResult = null;
                    _userdataUninstallError = null;
                    _internalInstallStep = 3;
                    committed = true;

                    acceptProgress = false;
                    SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.Prepared"));
                    AppendLocalizedLog(M("Dynamic.UserdataUninstall.Status.PreparedLog"));
                }
                catch (OperationCanceledException)
                {
                    SetText(BootMaintenancePrepareStatusText, M("Dynamic.UserdataUninstall.Status.PrepareFailed"));
                    AppendLocalizedLog(M("Dynamic.UserdataUninstall.Status.PrepareCancelled"));
                }
                catch (Exception exception)
                {
                    acceptProgress = false;
                    _userdataUninstallError = exception.Message;
                    RenderUserdataUninstallPreparationError();
                    AppendLocalizedLog(M(
                        "Dynamic.RawValue",
                        $"{exception.GetType().FullName}: {exception.Message}"));
                }
                finally
                {
                    acceptProgress = false;
                    if (!committed)
                    {
                        backend?.Dispose();
                        menu?.Dispose();
                        original?.Dispose();
                    }
                }
            });
        }
        finally
        {
            _userdataUninstallOperationActive = false;
            SetText(BootMaintenanceProgressText, "Dynamic.RawValue", string.Empty);
            BootMaintenanceProgressBar.IsIndeterminate = false;
            BootMaintenanceProgressBar.Value = _userdataUninstallBackend is not null
                && HasPreparedUninstallBootPlan ? 100 : 0;
            RenderModernUserdataUninstallPages();
            RefreshModernUserdataUninstallActions();
        }
    }
    private void RenderUserdataUninstallPreparationError()
    {
        if (string.IsNullOrWhiteSpace(_userdataUninstallError)) return;
        var message = _userdataUninstallError.StartsWith("requires-current-bootloader-state:", StringComparison.Ordinal)
            ? M("Dynamic.UserdataUninstall.Status.LockCheckRequired")
            : M("Dynamic.UserdataUninstall.Status.PrepareFailureDetail", _userdataUninstallError);
        SetText(BootMaintenancePrepareStatusText, message);
    }

}
