using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace GamonoYTDL;

class Program
{
    static async Task Main(string[] args)
    {
        bool autoExit = args.Length > 0;

        try
        {
            string? videoUrl = null;
            string downloadType = "audio";

            // === MODO PROTOCOLO ===
            if (args.Length > 0)
                (videoUrl, downloadType) = ParseCustomUrl(args[0]);

            // === MODO CONSOLE ===
            if (string.IsNullOrWhiteSpace(videoUrl))
            {
                Console.Write("URL do YouTube: ");
                videoUrl = Console.ReadLine();

                Console.Write("Tipo (audio/video) [audio]: ");
                var input = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(input))
                    downloadType = input.ToLower();
            }

            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new Exception("URL inválida.");

            var youtube = new YoutubeClient();

            if (downloadType == "video")
            {
                // ===================== VÍDEO =====================
                Console.WriteLine();
                Console.WriteLine("[1/4] Obtendo informações do vídeo");

                var video = await youtube.Videos.GetAsync(videoUrl);
                var manifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);

                var tempDir = Path.GetTempPath();
                var safeTitle = SanitizeFileName(video.Title);

                var videoStream = manifest
                    .GetVideoOnlyStreams()
                    .Where(s => s.Container == Container.Mp4)
                    .GetWithHighestVideoQuality();

                var audioStream = manifest
                    .GetAudioOnlyStreams()
                    .GetWithHighestBitrate();

                if (videoStream == null || audioStream == null)
                    throw new Exception("Streams de vídeo/áudio não encontradas.");

                var videoPath = Path.Combine(tempDir, $"{safeTitle}.video.mp4");
                var audioPath = Path.Combine(tempDir, $"{safeTitle}.audio.m4a");
                var finalPath = Path.Combine(tempDir, $"{safeTitle}.mp4");

                Console.WriteLine();
                Console.WriteLine("[2/4] Baixando vídeo");
                await DownloadWithProgress(youtube, videoStream, videoPath, "Vídeo");

                Console.WriteLine();
                Console.WriteLine("[3/4] Baixando áudio");
                await DownloadWithProgress(youtube, audioStream, audioPath, "Áudio");

                Console.WriteLine();
                Console.WriteLine("[4/4] Juntando vídeo + áudio");
                var ffmpeg = ExtractFfmpeg();
                MuxVideoAudio(videoPath, audioPath, finalPath, ffmpeg);

                File.Delete(videoPath);
                File.Delete(audioPath);

                MoveToDownloads(finalPath);
            }
            else
            {
                // ===================== ÁUDIO =====================
                Console.WriteLine();
                Console.WriteLine("[1/3] Obtendo informações do vídeo");

                var video = await youtube.Videos.GetAsync(videoUrl);
                var manifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);

                var tempDir = Path.GetTempPath();
                var safeTitle = SanitizeFileName(video.Title);

                var audioStream = manifest
                    .GetAudioOnlyStreams()
                    .GetWithHighestBitrate();

                if (audioStream == null)
                    throw new Exception("Stream de áudio não encontrada.");

                var tempAudio = Path.Combine(tempDir, $"{safeTitle}.{audioStream.Container}");
                var mp3Path = Path.Combine(tempDir, $"{safeTitle}.mp3");

                Console.WriteLine();
                Console.WriteLine("[2/3] Baixando áudio");
                await DownloadWithProgress(youtube, audioStream, tempAudio, "Áudio");

                Console.WriteLine();
                Console.WriteLine("[3/3] Convertendo para MP3");
                var ffmpeg = ExtractFfmpeg();
                ConvertToMp3(tempAudio, mp3Path, ffmpeg);

                File.Delete(tempAudio);
                MoveToDownloads(mp3Path);
            }

            Console.WriteLine();
            Console.WriteLine("✓ Concluído com sucesso");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ Erro: {ex.Message}");
        }

        if (!autoExit)
        {
            Console.WriteLine();
            Console.WriteLine("Pressione qualquer tecla para sair...");
            Console.ReadKey();
        }
    }

    // ===================== PROGRESSO =====================

    static async Task DownloadWithProgress(
        YoutubeClient yt,
        IStreamInfo stream,
        string output,
        string label
    )
    {
        var totalMb = stream.Size.MegaBytes;
        double lastPercent = -1;

        var progress = new Progress<double>(p =>
        {
            var percent = Math.Floor(p * 100);
            if (percent != lastPercent)
            {
                var current = totalMb * p;
                Console.Write(
                    $"\r{label}: {percent}% ({current:0.0} / {totalMb:0.0} MB)"
                );
                lastPercent = percent;
            }
        });

        await yt.Videos.Streams.DownloadAsync(stream, output, progress);
        Console.WriteLine();
    }

    // ===================== FFMPEG =====================

    static void ConvertToMp3(string input, string output, string ffmpeg)
    {
        RunFfmpeg(ffmpeg, $"-i \"{input}\" -vn -b:a 192k \"{output}\" -y");
    }

    static void MuxVideoAudio(string video, string audio, string output, string ffmpeg)
    {
        RunFfmpeg(
            ffmpeg,
            $"-i \"{video}\" -i \"{audio}\" -c:v copy -c:a aac \"{output}\" -y"
        );
    }

    static void RunFfmpeg(string ffmpeg, string args)
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        p?.WaitForExit();
        if (p == null || p.ExitCode != 0)
            throw new Exception("Erro ao executar ffmpeg.");
    }

    // ===================== UTIL =====================

    static (string? url, string type) ParseCustomUrl(string raw)
    {
        if (!raw.StartsWith("gamono-ytdl:", StringComparison.OrdinalIgnoreCase))
            return (null, "audio");

        var clean = raw.Replace("gamono-ytdl:", "", StringComparison.OrdinalIgnoreCase);
        if (clean.StartsWith("//")) clean = clean[2..];

        var uri = new Uri("http://x/?" + clean.Split('?', 2)[1]);
        var query = HttpUtility.ParseQueryString(uri.Query);

        return (query["url"], query["type"] ?? "audio");
    }

    static string ExtractFfmpeg()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = "GamonoYTDL.Resources.ffmpeg.exe";
        var path = Path.Combine(Path.GetTempPath(), "ffmpeg.exe");

        if (File.Exists(path))
            return path;

        using var res = asm.GetManifestResourceStream(name)
            ?? throw new Exception("ffmpeg embutido não encontrado.");

        using var file = File.Create(path);
        res.CopyTo(file);
        return path;
    }

    static void MoveToDownloads(string file)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );

        Directory.CreateDirectory(downloads);

        var dest = GetUniqueFilePath(
            Path.Combine(downloads, Path.GetFileName(file))
        );

        File.Move(file, dest);
        Console.WriteLine($"Arquivo salvo em: {dest}");
    }

    static string SanitizeFileName(string name)
    {
        var regex = new Regex($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]");
        return regex.Replace(name, "_");
    }

    static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        int i = 1;
        while (true)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
            i++;
        }
    }
}