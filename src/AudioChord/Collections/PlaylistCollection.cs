﻿using System;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using AudioChord.Collections.Models;

namespace AudioChord.Collections
{
    public class PlaylistCollection
    {
        private IMongoCollection<PlaylistStub> collection;
        private SongCollection songRepository;

        internal PlaylistCollection(IMongoDatabase database, SongCollection repository)
        {
            collection = database.GetCollection<PlaylistStub>(nameof(Playlist));
            songRepository = repository;
        }

        /// <summary>
        /// Find a playlist in the database
        /// </summary>
        /// <param name="id">The id of the playlist to look for</param>
        /// <returns>A <see cref="Playlist"/> with songs</returns>
        /// <exception cref="ArgumentException">There was no playlist with that id found in the database</exception>
        public async Task<Playlist> GetPlaylistAsync(ObjectId id)
        {
            var result = (await collection.FindAsync(filter => filter.Id == id))
                .FirstOrDefault() ?? throw new ArgumentException($"The Playlist id {id} was not found in the database");

            return await ConvertPlaylistAsync(result);
        }

        public async Task<IEnumerable<Playlist>> GetAllAsync()
        {
            List<Playlist> all = new List<Playlist>();

            foreach(PlaylistStub stub in (await collection.FindAsync(FilterDefinition<PlaylistStub>.Empty)).ToEnumerable())
            {
                all.Add(
                    await ConvertPlaylistAsync(stub)
                );
            }

            return all;
        }

        public Task UpdateAsync(Playlist playlist) 
            => collection.ReplaceOneAsync(filter => filter.Id == playlist.Id, playlist.ConvertToDatabaseRepresentation(), new UpdateOptions() { IsUpsert = true });

        public Task DeleteAsync(ObjectId id) 
            => collection.DeleteOneAsync(filter => filter.Id == id);

        private async Task<Playlist> ConvertPlaylistAsync(PlaylistStub stub)
        {
            List<ISong> playlist = new List<ISong>();

            await Task.WhenAll(
                stub.SongIds.Select(async (songId) =>
                {
                    ISong tempSong = null;
                    if (await songRepository.TryGetSongAsync(songId, (song) => { tempSong = song; }))
                        playlist.Add(tempSong);
                })
            );

            return new Playlist(stub.Id, playlist);
        }
    }
}
