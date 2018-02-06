﻿using MongoDB.Driver;
using Shared.Music.Collections;
using Shared.Music.Collections.Models;

namespace Shared.Music
{
    public class MusicService
    {
        private PlaylistCollection playlistCollection;
        private SongCollection songCollection;
        private OpusCollection opusCollection;

        public MusicService(MusicServiceConfig config)
        {
            MongoClient client = new MongoClient($"mongodb://{config.Username}:{config.Password}@localhost:27017/sharedmusic");
            IMongoDatabase database = client.GetDatabase("sharedmusic");

            playlistCollection = new PlaylistCollection(database.GetCollection<Playlist>(typeof(Playlist).Name));
            songCollection = new SongCollection(database.GetCollection<Song>(typeof(Song).Name));
            opusCollection = new OpusCollection(database);
        }
    }
}
