using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class AuxiliaryWorkflowNavigationTests
{
    [Fact]
    public void FirstStep_IsBlockedWithoutInputReadiness()
    {
        var nav = new AuxiliaryWorkflowNavigation(0, false, false, false, false);
        Assert.True(nav.CanSelect(0));
        Assert.False(nav.CanSelect(1));
        Assert.False(nav.CanSelect(2));
    }

    [Fact]
    public void PreparedInput_AllowsFirstForwardStep()
    {
        var nav = new AuxiliaryWorkflowNavigation(0, false, false, true, false);
        Assert.True(nav.CanSelect(1));
        Assert.False(nav.CanSelect(2));
    }

    [Fact]
    public void SecondForwardStep_RequiresConfirmationReadiness()
    {
        var ready = new AuxiliaryWorkflowNavigation(1, false, false, true, true);
        var blocked = new AuxiliaryWorkflowNavigation(1, false, false, true, false);
        Assert.True(ready.CanSelect(2));
        Assert.False(blocked.CanSelect(2));
    }

    [Fact]
    public void Backwards_IsAllowedWhileIdle()
    {
        var nav = new AuxiliaryWorkflowNavigation(2, false, false, false, false);
        Assert.True(nav.CanSelect(1));
        Assert.True(nav.CanSelect(0));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BusyOrFrozen_BlocksEveryMove(bool busy, bool frozen)
    {
        var nav = new AuxiliaryWorkflowNavigation(1, busy, frozen, true, true);
        Assert.False(nav.CanSelect(0));
        Assert.False(nav.CanSelect(1));
        Assert.False(nav.CanSelect(2));
        Assert.False(nav.CanSelect(3));
        Assert.False(nav.CanSelect(-1));
    }
}
