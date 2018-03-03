﻿using Newtonsoft.Json.Linq;
using Shared.Music.Collections.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Shared.Music.Processors
{
    /// <summary>
    /// Convert discord url links to opus audio data
    /// </summary>
    internal class DiscordProcessor
    {
        private FFMpegEncoder encoder = new FFMpegEncoder();
        private string filename;

        public SongMetadata Metadata { get; private set; }

        internal static async Task<DiscordProcessor> RetrieveAsync(string url, string uploader)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentNullException("The url given is either null or empty!");

            DiscordProcessor processor = new DiscordProcessor();

            await processor.GetMetadataAsync(url, uploader);

            return processor;
        }

        private async Task<string> ProbeFileAsync(string filename)
        {
            TaskCompletionSource<int> awaitExitSource = new TaskCompletionSource<int>();
            string json = null;

            using (Process process = new Process()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = "ffprobe",
                    Arguments = $"-i {filename} -hide_banner -show_format -print_format json -v quiet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            })
            {
                process.Exited += (obj, args) => {
                    awaitExitSource.SetResult(process.ExitCode);
                };

                process.Start();

                json = await process.StandardOutput.ReadToEndAsync();
                int exitCode = await awaitExitSource.Task;
            }

            return json;
        }

        private async Task GetMetadataAsync(string url, string uploader)
        {
            filename = url.Substring(url.LastIndexOf('/'));
            string songName = Path.GetFileNameWithoutExtension(filename);
            TimeSpan length;

            string json = await ProbeFileAsync(filename);

            JObject root = JObject.Parse(json);
            if (root["format"].HasValues)
            {
                length = TimeSpan.FromSeconds(root["format"]["duration"].Value<double>());

                if (root["format"]["tags"].HasValues && ((JObject)root["format"]["tags"]).TryGetValue("title", out JToken title))
                {
                    songName = title.ToString();
                }

                Metadata = new SongMetadata(songName, length, uploader, url);
                return;
            }
            throw new ArgumentException("Unable to probe file.");
        }

        internal async Task<Stream> ProcessAudioAsync()
        {
            return await encoder.ProcessAsync(filename);
        }
    }
}
