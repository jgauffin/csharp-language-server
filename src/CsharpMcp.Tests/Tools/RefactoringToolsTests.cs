using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Microsoft.Build.Locator;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

/// <summary>
/// Rename tests operate on a private copy of the fixture to avoid
/// mutating the shared files on disk between test runs.
/// </summary>
public class RefactoringToolsTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private RoslynWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        _tempDir = Path.Combine(Path.GetTempPath(), "CsharpMcpTests_" + Guid.NewGuid());
        CopyDirectory(FixturePath, _tempDir);
        _workspace = await RoslynWorkspace.LoadAsync(_tempDir);
    }

    public Task DisposeAsync()
    {
        _workspace.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ── rename_preview ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenamePreview_Calculator_Add_ShowsChangesAcrossProjects()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "Sum");

        preview.NewName.ShouldBe("Sum");
        preview.Changes.ShouldNotBeEmpty();
        // Changes must span multiple projects
        var projects = preview.AffectedFiles
            .Select(f => f.Split(Path.DirectorySeparatorChar).SkipWhile(p => p != "LibA" && p != "LibB" && p != "App").FirstOrDefault())
            .Where(p => p is not null)
            .Distinct()
            .ToList();
        projects.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task RenamePreview_DoesNotWriteToDisk()
    {
        var calcPath = FilePath("LibA", "Calculator.cs");
        var originalContent = await File.ReadAllTextAsync(calcPath);
        var pos = new Position(calcPath, Line: 7, Column: 16);

        await RefactoringTools.RenamePreviewAsync(_workspace.Solution, pos, "Sum");

        var contentAfter = await File.ReadAllTextAsync(calcPath);
        contentAfter.ShouldBe(originalContent);
    }

    // ── rename_symbol ──────────────────────────────────────────────────────

    [Fact]
    public async Task RenameSymbol_Calculator_Add_WritesNewNameToDisk()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var result = await RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum");

        result.NewName.ShouldBe("Sum");

        // Declaration site updated
        var calcContent = await File.ReadAllTextAsync(FilePath("LibA", "Calculator.cs"));
        calcContent.ShouldContain("public int Sum(");
        calcContent.ShouldNotContain("public int Add(");
    }

    [Fact]
    public async Task RenameSymbol_Calculator_Add_UpdatesAllCallSites()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        await RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum");

        // App/Program.cs uses calc.Add(1, 2)
        var appContent = await File.ReadAllTextAsync(FilePath("App", "Program.cs"));
        appContent.ShouldContain("calc.Sum(");
        appContent.ShouldNotContain("calc.Add(");

        // LibB/Dog.cs uses _calculator.Add(4, otherLegs)
        var libBContent = await File.ReadAllTextAsync(FilePath("LibB", "Dog.cs"));
        libBContent.ShouldContain("_calculator.Sum(");
        libBContent.ShouldNotContain("_calculator.Add(");
    }

    [Fact]
    public async Task RenameSymbol_CrossProject_Type_UpdatesAllReferences()
    {
        // Rename Calculator class itself
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 4, Column: 14);

        await RefactoringTools.RenameSymbolAsync(_workspace, pos, "MathHelper");

        // The declaration file is renamed to match the new type name.
        var calcContent = await File.ReadAllTextAsync(FilePath("LibA", "MathHelper.cs"));
        calcContent.ShouldContain("public class MathHelper");

        var appContent = await File.ReadAllTextAsync(FilePath("App", "Program.cs"));
        appContent.ShouldContain("new MathHelper()");

        var libBContent = await File.ReadAllTextAsync(FilePath("LibB", "Dog.cs"));
        libBContent.ShouldContain("MathHelper");
    }

    [Fact]
    public async Task RenameSymbol_Type_RenamesFileAndLeavesNoDuplicate()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 4, Column: 14);

        await RefactoringTools.RenameSymbolAsync(_workspace, pos, "MathHelper");

        // The old file is gone, the new file exists in the SAME folder (not the root).
        File.Exists(FilePath("LibA", "Calculator.cs")).ShouldBeFalse();
        File.Exists(FilePath("LibA", "MathHelper.cs")).ShouldBeTrue();

        // No stray copy dumped at the solution/project root.
        File.Exists(Path.Combine(_tempDir, "MathHelper.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "Calculator.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "LibA", "LibA", "MathHelper.cs")).ShouldBeFalse();

        // Exactly one file on disk declares the renamed type.
        var declaringFiles = Directory.GetFiles(_tempDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("public class MathHelper"))
            .ToList();
        declaringFiles.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RenameSymbol_RenamePreview_ResultMatchesActualChanges()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var preview = await RefactoringTools.RenamePreviewAsync(_workspace.Solution, pos, "Sum");
        var actual = await RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum");

        actual.AffectedFiles.Count.ShouldBe(preview.AffectedFiles.Count);
        actual.Changes.Count.ShouldBe(preview.Changes.Count);
    }

    // ── output size guarantees ─────────────────────────────────────────────

    [Fact]
    public async Task RenamePreview_OfMethod_ReportsOnlyIdentifierSwaps_NotWholeFileContents()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "Sum");

        preview.Changes.ShouldNotBeEmpty();
        foreach (var change in preview.Changes)
        {
            change.OldText.ShouldBe("Add");
            change.NewText.ShouldBe("Sum");
        }
    }

    [Fact]
    public async Task RenamePreview_OfTypeThatAlsoRenamesItsFile_ReportsOnlyIdentifierSwaps()
    {
        // Renaming a type also renames its document, which is the case where Roslyn's
        // change ranges collapse to a single span covering the entire file.
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 4, Column: 14);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "MathHelper");

        preview.Changes.ShouldNotBeEmpty();
        foreach (var change in preview.Changes)
        {
            change.OldText.ShouldBe("Calculator");
            change.NewText.ShouldBe("MathHelper");
        }
    }

    [Fact]
    public async Task RenamePreview_NeverEmitsSourceLinesSuchAsNamespaceDeclarations()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 4, Column: 14);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "MathHelper");

        var rendered = TextFormatter.Format(preview);

        // Whole-file leakage would drag surrounding source into the output.
        rendered.ShouldNotContain("namespace LibA");
        rendered.ShouldNotContain("public int Multiply");
        rendered.ShouldNotContain("<summary>");
    }

    [Fact]
    public async Task RenameSymbol_WithoutFileRename_ReportsNoRenamedFiles()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var result = await RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum");

        result.RenamedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenameResultOutput_AnnouncesFileMoves_SoAgentsCanUpdateCachedPaths()
    {
        // Reporting contract for a moved file, exercised directly: an executed rename that
        // moves a document must name both paths, because that is the one effect a caller
        // cannot infer from the edit list.
        var result = new RefactoringTools.RenamePreview(
            "MathHelper",
            Changes: [],
            AffectedFiles: [],
            RenamedFiles: [new RefactoringTools.RenamedFile(
                FilePath("LibA", "Calculator.cs"), FilePath("LibA", "MathHelper.cs"))],
            EditsPerFile: []);

        var rendered = TextFormatter.FormatRenameResult(result);

        rendered.ShouldContain("Calculator.cs");
        rendered.ShouldContain("MathHelper.cs");
        rendered.ShouldContain("→");
    }

    [Fact]
    public async Task RenamePreview_ListsEveryEditSiteWithItsLocation()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "Sum");

        var rendered = TextFormatter.Format(preview);

        // Declaration site plus the App call site, each addressable as file:line:col.
        rendered.ShouldContain("Calculator.cs:7:16");
        rendered.ShouldContain("Program.cs:15:27");
        rendered.ShouldContain("\"Add\" → \"Sum\"");
    }

    [Fact]
    public async Task RenameExecuteOutput_IsAReceipt_NotAListOfEveryEdit()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var result = await RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum");
        var rendered = TextFormatter.FormatRenameResult(result);

        rendered.ShouldContain("Renamed");
        rendered.ShouldContain("Sum");
        // Per-file tallies, not per-edit lines with column positions.
        rendered.ShouldContain("edit");
        rendered.ShouldNotContain("Calculator.cs:7:16");
    }

    [Fact]
    public async Task RenamePreview_TruncatesLongChangeListsAndSaysHowManyRemain()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var preview = await RefactoringTools.RenamePreviewAsync(
            _workspace.Solution, pos, newName: "Sum");

        // Force truncation well below the real change count.
        var rendered = TextFormatter.Format(preview, maxChangesShown: 1);

        rendered.ShouldContain("and ");
        rendered.ShouldContain("more");
    }

    [Fact]
    public async Task RenameSymbol_WithCompilationErrors_ThrowsBeforeWriting()
    {
        // Introduce a syntax error in LibA
        var calcPath = FilePath("LibA", "Calculator.cs");
        var original = await File.ReadAllTextAsync(calcPath);
        await File.WriteAllTextAsync(calcPath, original + "\nthis is not valid csharp!!!");

        // Reload workspace to pick up the error
        _workspace.Dispose();
        _workspace = await RoslynWorkspace.LoadAsync(_tempDir);

        var pos = new Position(calcPath, Line: 7, Column: 16);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => RefactoringTools.RenameSymbolAsync(_workspace, pos, "Sum"));

        ex.Message.ShouldContain("compilation errors");
    }

    [Fact]
    public async Task RenameSymbol_InterfaceMember_UpdatesImplementationAndCallSites()
    {
        // IAnimal.Speak declared in LibA/Animal.cs line 8
        var pos = new Position(FilePath("LibA", "Animal.cs"), Line: 8, Column: 12);

        await RefactoringTools.RenameSymbolAsync(_workspace, pos, "MakeNoise");

        var animalContent = await File.ReadAllTextAsync(FilePath("LibA", "Animal.cs"));
        animalContent.ShouldContain("MakeNoise()");
        animalContent.ShouldNotContain("string Speak()");

        var dogContent = await File.ReadAllTextAsync(FilePath("LibB", "Dog.cs"));
        dogContent.ShouldContain("MakeNoise()");
        dogContent.ShouldNotContain("Speak()");

        var appContent = await File.ReadAllTextAsync(FilePath("App", "Program.cs"));
        appContent.ShouldContain("MakeNoise()");
    }

    // ── format_document ────────────────────────────────────────────────────

    [Fact]
    public async Task FormatDocument_ReturnsFormattedText()
    {
        var result = await RefactoringTools.FormatDocumentAsync(
            _workspace.Solution, FilePath("LibA", "Calculator.cs"));

        result.ShouldNotBeNullOrWhiteSpace();
        result.ShouldContain("public int Add");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "MultiProject"));

    private string FilePath(string project, string file) =>
        Path.Combine(_tempDir, project, file);

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
