﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PsvDecryptCore.Common;
using PsvDecryptCore.Models;

namespace PsvDecryptCore.Services
{
    internal class DecryptionEngine
    {
        private readonly LoggingService _loggingService;
        private readonly PsvInformation _psvInformation;

        public DecryptionEngine(PsvInformation psvInformation,
            LoggingService loggingService)
        {
            _psvInformation = psvInformation;
            _loggingService = loggingService;
        }

        public async Task StartAsync(ParallelOptions options = null)
        {
            options = options ?? new ParallelOptions {MaxDegreeOfParallelism = Environment.ProcessorCount};
            IEnumerable<Course> courses;
            using (var db = new PsvContext(_psvInformation))
            {
                courses = db.Courses;
            }
            foreach (var course in courses)
            {
                await _loggingService.LogAsync(LogLevel.Information, $"Processing course \"{course.Name}\"...")
                    .ConfigureAwait(false);
                // Checks
                string courseSource = Path.Combine(_psvInformation.CoursesPath, course.Name);
                string courseOutput = Path.Combine(_psvInformation.Output, course.Title.Humanize(LetterCasing.Title));
                if (!Directory.Exists(courseSource))
                {
                    await _loggingService.LogAsync(LogLevel.Warning,
                        $"Courses directory for \"{course.Name}\" not found. Skipping...").ConfigureAwait(false);
                    continue;
                }

                if (!Directory.Exists(courseOutput)) Directory.CreateDirectory(courseOutput);

                // Course image copy
                await CopyCourseImageAsync(courseSource, courseOutput).ConfigureAwait(false);

                // Write course info
                await WriteCourseInfoAsync(course, courseOutput).ConfigureAwait(false);

                List<Module> modules;
                using (var psvContext = new PsvContext(_psvInformation))
                {
                    modules = await psvContext.Modules.Where(x => x.CourseName == course.Name).ToListAsync()
                        .ConfigureAwait(false);
                }
                await _loggingService.LogAsync(LogLevel.Information,
                    $"Found {modules.Count} modules under course \"{course.Name}\"...").ConfigureAwait(false);
                foreach (var module in modules)
                {
                    // Preps
                    await _loggingService.LogAsync(LogLevel.Information, $"Processing module: {module.Name}...")
                        .ConfigureAwait(false);
                    string moduleHash = await GetModuleHashAsync(module.Name, module.AuthorHandle)
                        .ConfigureAwait(false);
                    string moduleOutput = Path.Combine(courseOutput,
                        $"{StringUtil.TitleToFileIndex(module.ModuleIndex)}. {StringUtil.TitleToFileName(module.Title)}");
                    string moduleSource = Path.Combine(courseSource, moduleHash);
                    if (!Directory.Exists(moduleOutput)) Directory.CreateDirectory(moduleOutput);

                    // Write module info
                    await WriteModuleInfoAsync(module, moduleOutput).ConfigureAwait(false);

                    // Process each clip
                    List<Clip> clips;
                    using (var psvContext = new PsvContext(_psvInformation))
                    {
                        clips = await psvContext.Clips.Where(x => x.ModuleId == module.Id).ToListAsync().ConfigureAwait(false);
                    }

                    // Bail if no courses are found in database
                    if (clips.Count == 0)
                    {
                        await _loggingService.LogAsync(LogLevel.Warning,
                                $"No corresponding clips found for module {module.Name}, skipping...")
                            .ConfigureAwait(false);
                        continue;
                    }

                    // Write clip info
                    await WriteClipInfoAsync(clips, moduleOutput).ConfigureAwait(false);

                    Parallel.ForEach(clips, options, async clip =>
                    {
                        string clipSource = Path.Combine(moduleSource, $"{clip.Name}.psv");
                        string clipName =
                            $"{StringUtil.TitleToFileIndex(clip.ClipIndex)}. {StringUtil.TitleToFileName(clip.Title)}";
                        string clipFilePath = Path.Combine(moduleOutput, $"{clipName}.mp4");

                        // Decrypt individual clip
                        await DecryptFileAsync(clipSource, clipFilePath).ConfigureAwait(false);

                        // Create subtitles for each clip
                        using (var psvContext = new PsvContext(_psvInformation))
                        {
                            var transcripts = psvContext.ClipTranscripts.Where(x => x.ClipId == clip.Id)
                                .ToList();
                            await BuildSubtitlesAsync(transcripts, moduleOutput, clipName).ConfigureAwait(false);
                        }
                    });
                }
            }
        }

        /// <summary>
        ///     Builds the <see cref="ClipTranscript" /> to SRT file.
        /// </summary>
        private async Task BuildSubtitlesAsync(IList<ClipTranscript> transcripts, string srtOutput,
            string srtName)
        {
            if (!transcripts.Any()) return;
            var transcriptBuilder = new StringBuilder();
            string transcriptFileOutput = Path.Combine(srtOutput, $"{srtName}.srt");
            int lineCount = 0;
            foreach (var transcript in transcripts)
            {
                lineCount++;
                transcriptBuilder.AppendLine(lineCount.ToString());
                string startTime = TimeSpan.FromMilliseconds(transcript.StartTime).ToString(@"hh\:mm\:ss");
                string endTime = TimeSpan.FromMilliseconds(transcript.EndTime).ToString(@"hh\:mm\:ss");
                transcriptBuilder.AppendLine($"{startTime},{transcript.StartTime % 1000}" +
                                             " --> " +
                                             $"{endTime},{transcript.EndTime % 1000}");
                transcriptBuilder.AppendLine(string.Join("\n",
                    transcript.Text.Replace("\r", "").Split('\n').Select(x => "- " + x)));
                transcriptBuilder.AppendLine();
            }
            await File.WriteAllTextAsync(transcriptFileOutput, transcriptBuilder.ToString()).ConfigureAwait(false);
            await _loggingService.LogAsync(LogLevel.Debug, $"Saved {srtName} subtitles...").ConfigureAwait(false);
        }

        /// <summary>
        ///     Gets the required module hash for course directory name.
        /// </summary>
        private static Task<string> GetModuleHashAsync(string name, string authorHandle)
        {
            using (var md5 = MD5.Create())
            {
                return Task.FromResult(Convert
                    .ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(name + "|" + authorHandle)))
                    .Replace('/', '_'));
            }
        }

        /// <summary>
        ///     Decrypts the selected file.
        /// </summary>
        private async Task DecryptFileAsync(string srcFile, string destFile)
        {
            if (string.IsNullOrWhiteSpace(srcFile) || !File.Exists(srcFile))
            {
                await _loggingService.LogAsync(LogLevel.Warning, $"Invalid source file {srcFile}, skipping...")
                    .ConfigureAwait(false);
                return;
            }

            using (var stream = new VirtualFileStream(srcFile))
            using (var output = new FileStream(destFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                output.SetLength(0);
                var buffer = stream.ReadAll();
                await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                await _loggingService.LogAsync(LogLevel.Information, $"Decrypted clip {Path.GetFileName(destFile)}.")
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Copies the course image if one exists.
        /// </summary>
        private async Task CopyCourseImageAsync(string courseSource, string courseOutput)
        {
            string imageSrc = Path.Combine(courseSource, "image.jpg");
            string imageOutput = Path.Combine(courseOutput, "image.jpg");
            if (!File.Exists(imageSrc))
            {
                await _loggingService.LogAsync(LogLevel.Warning, $"No course image found in {courseSource}, skipping.")
                    .ConfigureAwait(false);
                return;
            }
            if (!File.Exists(imageOutput))
            {
                File.Copy(imageSrc, imageOutput);
                await _loggingService.LogAsync(LogLevel.Debug, $"Copied course image to {imageOutput}.")
                    .ConfigureAwait(false);
            }
        }

        private async Task WriteCourseInfoAsync(Course courseInfo, string courseOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(courseInfo, Formatting.Indented);
            string output = Path.Combine(courseOutput, "course-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                await File.WriteAllTextAsync(output, serializedOutput).ConfigureAwait(false);
                await _loggingService.LogAsync(LogLevel.Debug,
                    $"Finished writing course info for {courseInfo.Name}...").ConfigureAwait(false);
                return;
            }
            await _loggingService.LogAsync(LogLevel.Warning, "Invalid course info, skipping...").ConfigureAwait(false);
        }

        private async Task WriteModuleInfoAsync(Module moduleInfo, string moduleOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(moduleInfo, Formatting.Indented);
            string output = Path.Combine(moduleOutput, "module-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                await File.WriteAllTextAsync(output, serializedOutput).ConfigureAwait(false);
                await _loggingService.LogAsync(LogLevel.Debug,
                    $"Finished writing module info for {moduleInfo.Name}...").ConfigureAwait(false);
                return;
            }
            await _loggingService.LogAsync(LogLevel.Warning, "Invalid module info, skipping...").ConfigureAwait(false);
        }

        private async Task WriteClipInfoAsync(IEnumerable<Clip> clipInfo, string clipOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(clipInfo, Formatting.Indented);
            string output = Path.Combine(clipOutput, "clip-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                await File.WriteAllTextAsync(output, serializedOutput).ConfigureAwait(false);
                await _loggingService.LogAsync(LogLevel.Debug,
                    $"Finished writing clip info for {clipInfo.FirstOrDefault().Name}...").ConfigureAwait(false);
                return;
            }
            await _loggingService.LogAsync(LogLevel.Warning, "Invalid clip info, skipping...").ConfigureAwait(false);
        }
    }
}