namespace FreedomInstaller.App;

internal readonly record struct AuxiliaryWorkflowNavigation(int Step, bool Busy, bool Frozen, bool InputReady, bool ConfirmationReady)
{
    internal bool CanSelect(int target)
    {
        if (Busy || Frozen) return false;
        if (target < 0 || target > 2) return false;
        if (target <= Step) return true;
        if (!InputReady) return false;
        if (target == 2 && !ConfirmationReady) return false;
        return true;
    }
}
