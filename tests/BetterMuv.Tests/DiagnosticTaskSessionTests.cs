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
    public void NewRunClearsPreviousBatchEntirelyButPreservesUnrelatedRootFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new DiagnosticTaskSession();
            string firstRun = session.BeginRun(root, false);
            string firstTask = session.Begin(root, "maze", false);
            File.WriteAllText(Path.Combine(firstTask, "scene.png"), "old");
            File.WriteAllText(Path.Combine(firstTask, "keep.txt"), "inside batch");
            File.WriteAllText(Path.Combine(session.ScreenshotsDirectory, "game-1.png"), "shot");
            File.WriteAllText(Path.Combine(root, "quest-roi-20260907-184447-001.png"), "legacy");
            File.WriteAllText(Path.Combine(root, "personal.png"), "keep");

            var next = new DiagnosticTaskSession();
            string secondRun = next.BeginRun(root, false);
            Assert.NotEqual(firstRun, secondRun);
            Assert.False(Directory.Exists(firstRun));
            Assert.False(Directory.Exists(firstTask));
            Assert.Equal(1, next.LastCleanupRemovedDirectories);
            Assert.True(File.Exists(Path.Combine(root, "personal.png")));
            Assert.False(File.Exists(Path.Combine(root, "quest-roi-20260907-184447-001.png")));
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
