using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Insira a URL do vídeo do YouTube:");
        string? videoUrl = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            Console.WriteLine("URL inválida. Encerrando...");
            return;
        }

        var youtube = new YoutubeClient();

        Console.WriteLine("Obtendo informações do vídeo...");
        var video = await youtube.Videos.GetAsync(videoUrl);

        Console.WriteLine($"Título: {video.Title}");
        Console.WriteLine("Baixando áudio...");

        var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);
        var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        if (audioStreamInfo == null)
        {
            Console.WriteLine("Não foi possível encontrar uma stream de áudio adequada.");
            return;
        }

        // Sanitize the file name
        var sanitizedTitle = SanitizeFileName(video.Title);
        var fileName = $"{sanitizedTitle}.{audioStreamInfo.Container}";
        var tempDirectory = Path.GetTempPath();
        var outputFilePath = Path.Combine(tempDirectory, fileName);

        await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, outputFilePath);
        Console.WriteLine("Áudio baixado com sucesso!");

        // Convert to MP3
        var mp3FileName = $"{sanitizedTitle}.mp3";
        var mp3FilePath = Path.Combine(tempDirectory, mp3FileName);

        Console.WriteLine("Convertendo áudio para MP3...");
        var ffmpegPath = ExtractFfmpeg();
        ConvertToMp3(outputFilePath, mp3FilePath, 128, ffmpegPath);

        // Delete the original audio file
        File.Delete(outputFilePath);

        // Move MP3 to Downloads folder
        var downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var destinationFilePath = GetUniqueFilePath(Path.Combine(downloadsFolder, mp3FileName));

        File.Move(mp3FilePath, destinationFilePath);

        Console.WriteLine($"Arquivo MP3 salvo em: {destinationFilePath}");
    }

    static string SanitizeFileName(string name)
    {
        // Remove invalid characters
        var invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
        var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
        return regex.Replace(name, "_");
    }

    static void ConvertToMp3(string inputFilePath, string outputFilePath, int bitrate, string ffmpegPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{inputFilePath}\" -b:a {bitrate}k \"{outputFilePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        if (process != null)
        {
            process.OutputDataReceived += (sender, args) => Console.WriteLine(args.Data);
            process.ErrorDataReceived += (sender, args) => Console.WriteLine(args.Data);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
        }
        else
        {
            Console.WriteLine("Falha ao iniciar o processo ffmpeg.");
        }
    }

    static string ExtractFfmpeg()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "YoutubeToMp3.Resources.ffmpeg.exe"; // Nome do recurso incorporado

        var tempPath = Path.Combine(Path.GetTempPath(), "ffmpeg.exe");

        using (var resourceStream = assembly.GetManifestResourceStream(resourceName))
        {
            if (resourceStream == null)
                throw new Exception("Não foi possível encontrar o ffmpeg embutido.");

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                resourceStream.CopyTo(fileStream);
            }
        }

        return tempPath;
    }

    static string GetUniqueFilePath(string filePath)
    {
        // Se o arquivo já existir, gera um novo nome
        var directory = Path.GetDirectoryName(filePath) ?? throw new ArgumentNullException(nameof(filePath), "Directory cannot be null");
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath) ?? throw new ArgumentNullException(nameof(filePath), "File name without extension cannot be null");
        var fileExtension = Path.GetExtension(filePath);
        var uniqueFilePath = filePath;
        int counter = 1;

        while (File.Exists(uniqueFilePath))
        {
            uniqueFilePath = Path.Combine(directory, $"{fileNameWithoutExtension} ({counter}){fileExtension}");
            counter++;
        }

        return uniqueFilePath;
    }
}
