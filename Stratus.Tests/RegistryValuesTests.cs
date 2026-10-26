using Microsoft.Win32;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class RegistryValuesTests
{
    [Fact]
    public void ValuesEqual_DWord_MatchesAcrossNumericTypes()
    {
        Assert.True(RegistryService.ValuesEqual(RegistryValueKind.DWord, 1, 1L));
        Assert.True(RegistryService.ValuesEqual(RegistryValueKind.DWord, 0, 0));
        Assert.False(RegistryService.ValuesEqual(RegistryValueKind.DWord, 1, 0));
    }

    [Fact]
    public void ValuesEqual_String_IsOrdinal()
    {
        Assert.True(RegistryService.ValuesEqual(RegistryValueKind.String, "abc", "abc"));
        Assert.False(RegistryService.ValuesEqual(RegistryValueKind.String, "abc", "ABC"));
        Assert.False(RegistryService.ValuesEqual(RegistryValueKind.String, "abc", null));
    }

    [Fact]
    public void ValuesEqual_Binary_ComparesContentNotReference()
    {
        Assert.True(RegistryService.ValuesEqual(RegistryValueKind.Binary, new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
        Assert.False(RegistryService.ValuesEqual(RegistryValueKind.Binary, new byte[] { 1, 2, 3 }, new byte[] { 1, 2 }));
    }

    [Fact]
    public void ValuesEqual_MultiString_ComparesSequence()
    {
        Assert.True(RegistryService.ValuesEqual(RegistryValueKind.MultiString, new[] { "a", "b" }, new[] { "a", "b" }));
        Assert.False(RegistryService.ValuesEqual(RegistryValueKind.MultiString, new[] { "a", "b" }, new[] { "b", "a" }));
    }

    [Fact]
    public void Normalize_ConvertsToRegistryTypes()
    {
        Assert.Equal(1, RegistryService.Normalize(RegistryValueKind.DWord, 1L));
        Assert.Equal(5_000_000_000L, RegistryService.Normalize(RegistryValueKind.QWord, 5_000_000_000L));
        Assert.Equal("text", RegistryService.Normalize(RegistryValueKind.String, "text"));
        Assert.Equal(new[] { "a", "b" }, RegistryService.Normalize(RegistryValueKind.MultiString, new[] { "a", "b" }));
    }
}
