using BetterMuv.Core;

namespace BetterMuv.Tests;

public class DiagnosticTaskSessionTests
{
    [Fact]
    public void PipelineTasksKeepSiblingScreenshotsInSameRun()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new DiagnosticTaskSession();
            string run = session.BeginRun(root, false);
            string maze = session.Begin(root, "maze", false);
            File.WriteAllText(Path.Combine(maze, "scene.png"), "maze");
            File.WriteAllText(Path.Combine(session.ScreenshotsDirectory, "game-1.png"), "shot");
            string mainQuest = session.Begin(root, "mainQuest", false);
            File.WriteAllText(Path.Combine(mainQuest, "scene.png"), "main");

            Assert.Equal(run, session.RunDirectoryPath);
            Assert.NotEqual(maze, mainQuest);
            Assert.True(File.Exists(Path.Combine(maze, "scene.png")));
            Assert.True(File.Exists(Path.Combine(mainQuest, "scene.png")));
            Assert.True(File.Exists(Path.Combine(run, "screenshots", "game-1.png")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NewRunKeepsPreviousBatchUntilRetentionCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new DiagnosticTaskSession();
            string firstRun = session.BeginRun(root, false);
            string firstTask = session.Begin(root, "maze", false);
            File.WriteAllText(Path.Combine(firstTask, "scene.png"), "old");
            File.WriteAllText(Path.Combine(session.ScreenshotsDirectory, "game-1.png"), "shot");
            File.WriteAllText(Path.Combine(root, "personal.png"), "keep");

            var next = new DiagnosticTaskSession();
            string secondRun = next.BeginRun(root, false);
            Assert.NotEqual(firstRun, secondRun);
            Assert.True(Directory.Exists(firstRun));
            Assert.True(File.Exists(Path.Combine(firstTask, "scene.png")));
            Assert.True(File.Exists(Path.Combine(root, "personal.png")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResumePreservesImagesWithinSameRun()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DiagnosticTaskSession();
            session.BeginRun(root, false);
            string first = session.Begin(root, "maze", false);
            File.WriteAllText(Path.Combine(first, "scene.png"), "old");
            Assert.Equal(session.RunDirectoryPath, session.BeginRun(root, true));
            Assert.Equal(first, session.Begin(root, "maze", true));
            Assert.True(File.Exists(Path.Combine(first, "scene.png")));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class LocalDataRetentionTests
{
    [Fact]
    public void CanonicalDiagnosticDirectoryStripsNestedRunTaskPaths()
    {
        string nested =
            @"C:\workspace\Better-Muv\bin\Debug\net10.0\diagnostics\run-a\task-maze-1\run-b\task-maze-2";
        Assert.Equal(
            @"C:\workspace\Better-Muv\bin\Debug\net10.0\diagnostics",
            ConfigStore.CanonicalDiagnosticDirectory(nested));
        Assert.Equal("diagnostics", ConfigStore.CanonicalDiagnosticDirectory("diagnostics"));
        Assert.Equal(
            "diagnostics",
            ConfigStore.CanonicalDiagnosticDirectory(@"diagnostics\run-old\task-maze"));
    }

    [Fact]
    public void CleanupRemovesLogsAndDiagnosticDirsOlderThanRetainDays()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(root, "logs");
        string diagnostics = Path.Combine(root, "diagnostics");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(diagnostics);
        try
        {
            string oldLog = Path.Combine(logs, "old.log");
            string newLog = Path.Combine(logs, "new.log");
            File.WriteAllText(oldLog, "old");
            File.WriteAllText(newLog, "new");
            File.SetLastWriteTime(oldLog, DateTime.Now.AddDays(-5));
            File.SetLastWriteTime(newLog, DateTime.Now.AddHours(-1));

            string oldRun = Path.Combine(diagnostics, "run-old");
            string newRun = Path.Combine(diagnostics, "run-new");
            Directory.CreateDirectory(oldRun);
            Directory.CreateDirectory(newRun);
            File.WriteAllText(Path.Combine(oldRun, "a.png"), "x");
            File.WriteAllText(Path.Combine(newRun, "b.png"), "y");
            Directory.SetLastWriteTime(oldRun, DateTime.Now.AddDays(-4));
            Directory.SetLastWriteTime(newRun, DateTime.Now.AddHours(-2));

            LocalDataRetention.CleanupOlderThanDays(3, logs, diagnostics);

            Assert.False(File.Exists(oldLog));
            Assert.True(File.Exists(newLog));
            Assert.False(Directory.Exists(oldRun));
            Assert.True(Directory.Exists(newRun));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
