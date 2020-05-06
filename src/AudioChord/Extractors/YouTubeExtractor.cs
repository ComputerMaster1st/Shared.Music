﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AudioChord.Processors;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

using YtPlaylist = YoutubeExplode.Playlists.Playlist;

namespace AudioChord.Extractors
{
    /// <summary>
    /// Extracts audio from youtube videos
    /// </summary>
    public class YouTubeExtractor : IAudioExtractor
    {
        private readonly YoutubeClient _client = new YoutubeClient();
        private readonly FFmpegEncoder _encoder = new FFmpegEncoder();

        public static string ProcessorPrefix { get; } = "YOUTUBE";

        public bool CanExtract(string source)
            => !(VideoId.TryParse(source) is null);

        public bool TryExtractSongId(string url, out SongId id)
        {
            VideoId? result = VideoId.TryParse(url);

            id = result.HasValue ? new SongId(ProcessorPrefix, result.Value) : null;
            return result.HasValue;
        }

        public Task<ISong> ExtractAsync(string url, ExtractorConfiguration configuration)
        {
            VideoId id = new VideoId(url);
            return ExtractSongAsync(id, configuration.MaxSongDuration);
        }

        private async Task<ISong> ExtractSongAsync(string videoId, TimeSpan maximumDuration)
        {
            if (VideoId.TryParse(videoId) is null)
                throw new ArgumentException("The videoId is not correctly formatted");

            // Retrieve the metadata of the video
            SongMetadata metadata = await GetVideoMetadataAsync(videoId);

            if (metadata.Duration > maximumDuration)
                throw new ArgumentOutOfRangeException(nameof(videoId), $"The duration of this song is longer than the maximum allowed duration! (~{Math.Round(maximumDuration.TotalMinutes)} minutes)");

            // Retrieve the actual video and convert it to opus
            foreach (AudioOnlyStreamInfo info in (await _client.Videos.Streams.GetManifestAsync(videoId)).GetAudioOnly())
            {
                // TODO: Implement logic for choosing a stream
                // This should be based on the encoding type and bitrate
                using (Stream youtubeStream = await _client.Videos.Streams.GetAsync(info))
                {
                    // Convert it to a Song class
                    // The processor should be responsible for prefixing the id with the correct type
                    return new Song(new SongId(ProcessorPrefix, videoId), metadata,
                        await _encoder.ProcessAsync(youtubeStream));
                }
            }
            
            throw new InvalidOperationException($"The given video at {metadata.Url} does not contain audio!");
        }
        
        private async Task<SongMetadata> GetVideoMetadataAsync(string youtubeVideoId)
        {
            Video videoInfo = await _client.Videos.GetAsync(youtubeVideoId);
            return new SongMetadata
            {
                Title = videoInfo.Title, 
                Duration = videoInfo.Duration, 
                Uploader = videoInfo.Author, 
                Url = videoInfo.Url
            };
        }

        /// <summary>
        /// Retrieve all video's out of a youtube playlist
        /// </summary>
        /// <param name="playlistLocation">The url to the targeted playlist</param>
        /// <returns></returns>
        internal IAsyncEnumerable<Video> ParsePlaylistAsync(Uri playlistLocation)
        {
            if (playlistLocation is null)
                throw new ArgumentNullException(nameof(playlistLocation), "The uri passed to this method is null");

            PlaylistId? playlistId = PlaylistId.TryParse(playlistLocation.ToString());
            if (playlistId is null)
                throw new ArgumentException("Invalid playlist url given", nameof(playlistLocation));

            // Retrieve all the videos
            return _client.Playlists.GetVideosAsync(playlistId.Value);
        }
    }
}