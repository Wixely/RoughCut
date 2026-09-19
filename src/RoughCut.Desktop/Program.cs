using CupriFace.Shell;
using RoughCut.Desktop;
using SkiaSharp;

try
{
    switch (args)
    {
        case [] or ["help"] or ["--help"]:
            Console.WriteLine("""
                RoughCut desktop review
                  review <project.json>
                  snapshot <project.json> <new-output.png>
                """);
            return 0;
        case ["review", var projectPath]:
            {
                var session = await DesktopReviewSession.LoadAsync(projectPath);
                await session.InitializePreviewAsync();
                DesktopHost.Run(new RoughCutReviewApp(session));
                return 0;
            }
        case ["snapshot", var projectPath, var outputPath]:
            {
                outputPath = Path.GetFullPath(outputPath);
                if (File.Exists(outputPath)) throw new IOException("Snapshot output already exists.");
                var session = await DesktopReviewSession.LoadAsync(projectPath);
                await session.InitializePreviewAsync();
                using var document = new RoughCutReviewApp(session).CreateDocument();
                using var image = document.RenderToImage(1280, 800, new SKColor(0x0b, 0x0f, 0x17));
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                data.SaveTo(stream);
                Console.WriteLine(outputPath);
                return 0;
            }
        default:
            Console.Error.WriteLine("Unknown command or arguments. Run roughcut-desktop help.");
            return 2;
    }
}
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
    InvalidOperationException or KeyNotFoundException or RoughCut.Core.ProjectValidationException or
    RoughCut.Core.RevisionConflictException or RoughCut.Media.MediaToolException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
