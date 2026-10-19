using Elans.Shared;

namespace Elans.Shared.Tests;

// The one list of file types that can run something when opened (the Outpost's warning and upload block, the Lodge's refusal).
public class DangerousFilesTests
{
    [Theory]
    [InlineData("setup.exe"), InlineData("SETUP.EXE"), InlineData("setup.exe. "), InlineData("a/b\\run.bat"), InlineData("x.ps1"), InlineData("link.lnk")]
    [InlineData("x.url"), InlineData("x.scf"), InlineData("disk.iso"), InlineData("disk.img"), InlineData("d.vhd"), InlineData("d.VHDX"), InlineData("help.chm")]
    [InlineData("a.msix"), InlineData("a.appx"), InlineData("a.appinstaller"), InlineData("x.library-ms"), InlineData("x.search-ms"), InlineData("x.settingcontent-ms")]
    [InlineData("addin.xll"), InlineData("setup.inf"), InlineData("x.ws"), InlineData("x.wsc"), InlineData("x.application"), InlineData("x.appref-ms")]
    public void Dangerous(string name) => Assert.True(DangerousFiles.IsDangerous(name));

    [Theory]
    [InlineData("shot.png"), InlineData("notes.txt"), InlineData("pack.zip"), InlineData("paper.pdf"), InlineData("noext"), InlineData(""), InlineData(null)]
    [InlineData("exe"), InlineData("my.exe.txt"), InlineData("folder.exe/readme.md"), InlineData("x.wsx"), InlineData("x.image")]
    public void Harmless(string name) => Assert.False(DangerousFiles.IsDangerous(name));

    [Fact]
    public void ContainsEveryTypeTheLodgeServerBlockedBefore()
    {
        // the server's FileRules.Blocked at 3.1.1 (server/Lodge.Server/FilesModule.cs): nothing it refused may be dropped
        foreach (var e in new[] { "exe", "dll", "com", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "msi", "msp", "scr", "cpl",
                                  "jar", "lnk", "reg", "hta", "sys", "drv", "pif", "application", "appref-ms" })
            Assert.Contains(e, DangerousFiles.Extensions);
    }

    [Fact]
    public void ExtensionsAreLowercaseWithoutADot()
    {
        foreach (var e in DangerousFiles.Extensions) { Assert.Equal(e.ToLowerInvariant(), e); Assert.DoesNotContain(".", e[..1]); Assert.False(string.IsNullOrWhiteSpace(e)); }
    }
}
