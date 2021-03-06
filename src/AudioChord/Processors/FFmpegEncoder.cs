﻿using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AudioChord.Processors
{
    // ReSharper disable once IdentifierTypo
    public class FFmpegEncoder
    {
        private ProcessStartInfo CreateEncoderInfo(string filePath, bool redirectInput = false)
            => new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    $"-i {filePath} -hide_banner -v quiet -ar 48k -codec:a libopus -b:a 128k -ac 2 -f opus pipe:1",
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true
            };

        public async Task<Stream> ProcessAsync(Stream input)
        {
            MemoryStream output = new MemoryStream();
            TaskCompletionSource<int> awaitExitSource = new TaskCompletionSource<int>();

            //create a new process for ffmpeg

            using (Process process = new Process
            {
                StartInfo = CreateEncoderInfo("pipe:0", true),
                EnableRaisingEvents = true
            })
            {
                process.Exited += (obj, args) => { awaitExitSource.SetResult(process.ExitCode); };

                process.Start();

                // NOTE: MUST be .WhenAny & Close input to prevent lockups
                await Task.WhenAny(input.CopyToAsync(process.StandardInput.BaseStream),
                    process.StandardOutput.BaseStream.CopyToAsync(output));
                process.StandardInput.Close();

                await awaitExitSource.Task;
            }

            output.Position = 0;
            return output;
        }

        public async Task<Stream> ProcessAsync(string filePath)
        {
            MemoryStream output = new MemoryStream();
            TaskCompletionSource<int> awaitExitSource = new TaskCompletionSource<int>();

            //create a new process for ffmpeg

            using (Process process = new Process
            {
                StartInfo = CreateEncoderInfo(filePath),
                EnableRaisingEvents = true
            })
            {
                process.Exited += (obj, args) =>
                {
                    awaitExitSource.SetResult(process.ExitCode);
                    File.Delete(filePath);
                };

                process.Start();

                await process.StandardOutput.BaseStream.CopyToAsync(output);

                await awaitExitSource.Task;
            }

            output.Position = 0;
            return output;
        }
    }
}