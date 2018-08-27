﻿using AudioChord.Collections;
using AudioChord.Collections.Models;
using AudioChord.Events;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;

namespace AudioChord
{
    public class MusicService
    {
        private PlaylistCollection playlistCollection;
        private SongCollection songCollection;
        private System.Timers.Timer resyncTimer = new System.Timers.Timer();

        public event EventHandler<ResyncEventArgs> ExecutedResync;
        public event EventHandler<ProcessedSongEventArgs> ProcessedSong;
        public event EventHandler<SongsExistedEventArgs> SongsExisted;
        public event EventHandler<SongProcessingCompletedEventArgs> SongProcessingCompleted;

        private Task QueueProcessor = null;
        private List<ProcessSongRequestInfo> QueueProcessorSongList = new List<ProcessSongRequestInfo>();
        private SemaphoreSlim QueueProcessorLock = new SemaphoreSlim(1, 1);
        private Dictionary<ulong, int> QueueGuildStatus = new Dictionary<ulong, int>();

        public MusicService(MusicServiceConfig config)
        {
            // This will tell NETCore 2.1 to use older httpclient. Newer version has SSL issues
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);

            //Use the builder to allow to connect to database without authentication
            MongoUrlBuilder connectionStringBuilder = new MongoUrlBuilder
            {
                DatabaseName = config.Database,
                Server = new MongoServerAddress(config.Hostname),

                Username = config.Username,
                Password = config.Password
            };

            MongoClient client = new MongoClient(connectionStringBuilder.ToMongoUrl());
            IMongoDatabase database = client.GetDatabase(config.Database);

            playlistCollection = new PlaylistCollection(database);
            songCollection = new SongCollection(database);

            if (config.EnableResync)
            {
                resyncTimer.Interval = TimeSpan.FromHours(12).TotalMilliseconds;
                resyncTimer.AutoReset = true;
                resyncTimer.Elapsed += async (obj, args) => await Resync();
                resyncTimer.Enabled = true;

                resyncTimer.Start();
            }
        }

        /// <summary>
        /// Create a new playlist.
        /// </summary>
        public async Task<Playlist> CreatePlaylist()
        {
            return await playlistCollection.Create();
        }

        /// <summary>
        /// Retrieve your playlist from database.
        /// </summary>
        /// <param name="playlistId">Place playlist Id to fetch.</param>
        /// <returns>A <see cref="Playlist"/> Playlist contains list of all available song Ids.</returns>
        public async Task<Playlist> GetPlaylistAsync(ObjectId playlistId)
        {
            return await playlistCollection.GetPlaylistAsync(playlistId);
        }

        /// <summary>
        /// Delete the playlist from database.
        /// </summary>
        /// <param name="playlistId">The playlist Id to delete.</param>
        public async Task DeletePlaylistAsync(ObjectId playlistId)
        {
            await playlistCollection.DeleteAsync(playlistId);
        }

        /// <summary>
        /// Fetch song metadata with opus stream from database.
        /// </summary>
        /// <param name="songId">The song Id.</param>
        /// <returns>A <see cref="Song"/> SongStream contains song metadata and opus stream. Returns null if nothing found.</returns>
        public async Task<Song> GetSongAsync(string songId)
        {
            SongData songData = await songCollection.GetSongAsync(songId);

            if (songData != null) return new Song(songData.Id, songData.Metadata, songData.OpusId, songCollection);
            else return null;
        }

        /// <summary>
        /// Get all songs in database.
        /// </summary>
        /// <returns>Dictionary of songs in database.</returns>
        public async Task<IEnumerable<Song>> GetAllSongsAsync()
        {
            List<Song> songList = new List<Song>();
            List<SongData> songDataList = await songCollection.GetAllAsync();

            if (songDataList.Count > 0)
                foreach (SongData data in songDataList)
                    songList.Add(new Song(data.Id, data.Metadata, data.OpusId, songCollection));

            return songList;
        }

        /// <summary>
        /// Get total bytes count in database.
        /// </summary>
        /// <returns>A double containing total bytes used.</returns>
        public async Task<double> GetTotalBytesUsedAsync()
        {
            return await songCollection.GetTotalBytesUsedAsync();
        }

        /// <summary>
        /// Manually restart the global queue processor in case of stalling.
        /// </summary>
        public QueueProcessorStatus RestartQueueProcessorAsync()
        {
            if (QueueProcessor == null || QueueProcessorSongList.Count < 1) 
                return QueueProcessorStatus.Idle;
            else if (QueueProcessor != null && QueueProcessor.Status == TaskStatus.WaitingForActivation)
                return QueueProcessorStatus.Running;
            
            QueueProcessor.Dispose();
            QueueGuildStatus.Clear();

            foreach (ProcessSongRequestInfo songInfo in QueueProcessorSongList)
            {
                foreach (var guildInfo in songInfo.GuildsRequested)
                    if (QueueGuildStatus.ContainsKey(guildInfo.Key)) QueueGuildStatus[guildInfo.Key]++;
                    else QueueGuildStatus.Add(guildInfo.Key, 1);
            }

            if (QueueProcessorLock.CurrentCount < 1) QueueProcessorLock.Release();
            QueueProcessor = Task.Factory.StartNew(ProcessSongListAsync, TaskCreationOptions.LongRunning);
            return QueueProcessorStatus.Restarted;
        }

        /// <summary>
        /// Cancel song processing for specified guild.
        /// </summary>
        public async Task CancelGuildMusicProcessingAsync(ulong guildId) {
            await QueueProcessorLock.WaitAsync();

            List<int> toBeRemoved = new List<int>();

            foreach (ProcessSongRequestInfo info in QueueProcessorSongList)
            {
                if (info.GuildsRequested.ContainsKey(guildId))
                    info.GuildsRequested.Remove(guildId);
                
                if (info.GuildsRequested.Count < 1)
                {
                    QueueGuildStatus.Remove(guildId);
                    toBeRemoved.Add(QueueProcessorSongList.IndexOf(info));
                }
            }

            foreach (int index in toBeRemoved) QueueProcessorSongList.RemoveAt(index);

            QueueProcessorLock.Release();
        }

        // ===============
        // ALL PROCESSOR BASED METHODS GO BELOW THIS COMMENT!
        // ===============

        /// <summary>
        /// Capture Youtube Video Id
        /// </summary>
        /// <param name="url">The youtube video url.</param>
        /// <returns>Return youtube video id.</returns>
        public string ParseYoutubeUrl(string url)
        {
            if (!YoutubeClient.TryParseVideoId(url, out string videoId))
                throw new ArgumentException("Video Url could not be parsed!");
            return videoId;
        }

        /// <summary>
        /// Download song from YouTube to database. (Note: Exceptions are to be expected.)
        /// </summary>
        /// <param name="url">The youtube video url.</param>
        /// <returns>Returns ObjectId of newly downloaded song.</returns>
        public async Task<Song> DownloadSongFromYouTubeAsync(string url)
        {
            string id = await songCollection.DownloadFromYouTubeAsync(url);
            return await GetSongAsync(id);
        }

        /// <summary>
        /// Download song from Discord to database. (Note: Exceptions are to be expected.)
        /// </summary>
        /// <param name="url">The discord attachment url.</param>
        /// <param name="uploader">The discord username.</param>
        /// <param name="attachmentId">The discord attachment Id.</param>
        /// <param name="autoDownload">Automatically download if non-existent.</param>
        /// <returns>Returns ObjectId of newly downloaded song.</returns>
        public async Task<Song> DownloadSongFromDiscordAsync(string url, string uploader, ulong attachmentId)
        {
            string id = await songCollection.DownloadFromDiscordAsync(url, uploader, attachmentId);
            return await GetSongAsync(id);
        }

        // ===============
        // ALL PLAYLIST HANDLING METHODS GO BELOW THIS COMMENT!
        // ===============

        /// <summary>
        /// Show how many songs are in the process queue.
        /// </summary>
        public int CurrentQueueLength {
            get { return QueueProcessorSongList.Count; }
        }

        /// <summary>
        /// Show how many songs from the guild are in the process queue.
        /// </summary>
        public int CurrentGuildQueueLength(ulong guildId)
        {
            return (QueueGuildStatus.ContainsKey(guildId)) ? QueueGuildStatus[guildId] : 0;
        }

        /// <summary>
        /// Download a list of YT songs to database.
        /// </summary>
        public async Task ProcessYTPlaylistAsync(List<string> youtubeUrls, ulong guildId, ulong textChannelId, Playlist playlist)
        {
            // Existing & Queued Counters for Guild's Request
            int installedSongs = 0;
            int existingSongs = 0;
            int queuedSongs = 0;
            int failedParsingSongs = 0;

            // Halt queue until playlist is processed
            await QueueProcessorLock.WaitAsync();

            // Create & queue up queue requests
            foreach (string url in youtubeUrls)
            {
                if (!YoutubeClient.TryParseVideoId(url, out string videoId))
                {
                    failedParsingSongs++;
                    continue;
                }

                // Check if song exists
                videoId = $"YOUTUBE#{videoId}";
                Song songData = await GetSongAsync(videoId);

                if (songData != null)
                {
                    // Add song to playlist if not already
                    if (!playlist.Songs.Contains(songData.Id))
                    {
                        playlist.Songs.Add(songData.Id);
                        installedSongs++;
                    }
                    else existingSongs++;

                    continue;
                }

                // \/ If doesn't exist \/
                ProcessSongRequestInfo info = QueueProcessorSongList.FirstOrDefault(i => i.VideoId == videoId);

                if (info != null)
                {
                    if (!info.GuildsRequested.ContainsKey(guildId))
                    {
                        info.GuildsRequested.Add(guildId, new Tuple<ulong, Playlist>(textChannelId, playlist));
                        queuedSongs++;
                    }
                }
                else 
                {
                    QueueProcessorSongList.Add(new ProcessSongRequestInfo(videoId, url, guildId, textChannelId, playlist));
                    queuedSongs++;
                }
            }

            if (installedSongs > 0) await playlist.SaveAsync();

            // Fire SongsAlreadyExisted Handler
            SongsExisted?.Invoke(this, new SongsExistedEventArgs(guildId, textChannelId, existingSongs, installedSongs, queuedSongs, failedParsingSongs));

            // Start Processing Song Queue
            if (QueueProcessorSongList.Count > 0)
            {
                if (QueueProcessor == null)
                    QueueProcessor = Task.Factory.StartNew(ProcessSongListAsync, TaskCreationOptions.LongRunning);
                else if (QueueProcessor != null && QueueProcessor.Status != TaskStatus.WaitingForActivation)
                {
                    QueueProcessor.Dispose();
                    QueueProcessor = Task.Factory.StartNew(ProcessSongListAsync, TaskCreationOptions.LongRunning);
                }

                // Add/Update The Guild's Music Processing Queue Status
                if (QueueGuildStatus.ContainsKey(guildId))
                    QueueGuildStatus[guildId] = (QueueGuildStatus[guildId] + queuedSongs);
                else
                    QueueGuildStatus.Add(guildId, queuedSongs);
            }

            QueueProcessorLock.Release();
        }

        /// <summary>
        /// Download a playlist of YT songs to database.
        /// </summary>
        public async Task<bool> ProcessYTPlaylistAsync(string youtubePlaylistUrl, ulong guildId, ulong textChannelId, Playlist playlist)
        {
            // Get YT playlist from user
            if (!YoutubeClient.TryParsePlaylistId(youtubePlaylistUrl, out string youtubePlaylistId)) return false;

            YoutubeClient youtubeClient = new YoutubeClient();
            YoutubeExplode.Models.Playlist youtubePlaylist = await youtubeClient.GetPlaylistAsync(youtubePlaylistId);
            List<string> youtubeUrls = new List<string>();

            // Create a list of all urls to process
            foreach (YoutubeExplode.Models.Video video in youtubePlaylist.Videos) youtubeUrls.Add($"https://youtu.be/{video.Id}");

            // Begin playlist processing
            await ProcessYTPlaylistAsync(youtubeUrls, guildId, textChannelId, playlist);
            return true;
        }

        // ===============
        // ALL PRIVATE METHODS GO BELOW THIS COMMENT!
        // ===============

        private async Task ProcessSongListAsync()
        {
            while (QueueProcessorSongList.Count > 0)
            {
                await QueueProcessorLock.WaitAsync();

                Song song = null;
                ProcessSongRequestInfo info = QueueProcessorSongList.FirstOrDefault();

                if (info == null) {
                    QueueProcessorLock.Release();
                    continue;
                }

                QueueProcessorSongList.Remove(info);

                try { song = await DownloadSongFromYouTubeAsync(info.VideoUrl); }
                catch { song = null; }

                foreach (var guildKeyValue in info.GuildsRequested)
                {
                    try
                    {
                        QueueGuildStatus[guildKeyValue.Key]--;

                        if (song == null)
                        {
                            // Trigger event upon 1 song completing
                            ProcessedSong?.Invoke(this, new ProcessedSongEventArgs(info.VideoId, null, guildKeyValue.Key, guildKeyValue.Value.Item1, QueueGuildStatus[guildKeyValue.Key], QueueProcessorSongList.Count));
                        }
                        else
                        {
                            Playlist playlist = guildKeyValue.Value.Item2;
                            playlist.Songs.Add(song.Id);
                            await playlist.SaveAsync();

                            // Trigger event upon 1 song completing
                            ProcessedSong?.Invoke(this, new ProcessedSongEventArgs(song.Id, song.Metadata.Name, guildKeyValue.Key, guildKeyValue.Value.Item1, QueueGuildStatus[guildKeyValue.Key], QueueProcessorSongList.Count));
                        }

                        // Remove QueueGuildStatus if completed
                        if (QueueGuildStatus.ContainsKey(guildKeyValue.Key) && QueueGuildStatus[guildKeyValue.Key] < 1) QueueGuildStatus.Remove(guildKeyValue.Key);
                    }
                    catch { }
                }
                
                QueueProcessorLock.Release();
            }

            SongProcessingCompleted?.Invoke(this, new SongProcessingCompletedEventArgs());
        }

        private async Task Resync()
        {
            await QueueProcessorLock.WaitAsync();

            List<SongData> expiredSongs = new List<SongData>();
            List<SongData> songList = await songCollection.GetAllAsync();
            int resyncedPlaylists = 0;
            int deletedDesyncedFiles = await songCollection.ResyncDatabaseAsync();
            DateTime startedAt = DateTime.Now;

            foreach (SongData song in songList)
                if (song.LastAccessed < DateTime.Now.AddDays(-90))
                    expiredSongs.Add(song);

            if (expiredSongs.Count > 0)
            {
                List<Playlist> playlists = await playlistCollection.GetAllAsync();

                foreach (Playlist playlist in playlists)
                {
                    int removedSongs = 0;

                    foreach (SongData song in expiredSongs)
                        if (playlist.Songs.Contains(song.Id))
                        {
                            removedSongs++;
                            playlist.Songs.Remove(song.Id);                            
                        }

                    if (removedSongs > 0)
                    {
                        await playlistCollection.UpdateAsync(playlist);
                        resyncedPlaylists++;
                    }
                }

                foreach (SongData song in expiredSongs)
                    await songCollection.DeleteSongAsync(song);
            }

            QueueProcessorLock.Release();

            //only invoke the eventhandler if somebody is subscribed to the event
            ExecutedResync?.Invoke(this, new ResyncEventArgs(startedAt, deletedDesyncedFiles, expiredSongs.Count, resyncedPlaylists));
        }
    }
}