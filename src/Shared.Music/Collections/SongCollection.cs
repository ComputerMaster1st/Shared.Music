﻿using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using Shared.Music.Collections.Models;
using System;
using System.Threading.Tasks;

namespace Shared.Music.Collections
{
    internal class SongCollection
    {
        private IMongoCollection<Song> collection;
        private GridFSBucket bucket;

        internal SongCollection(IMongoDatabase database)
        {
            collection = database.GetCollection<Song>(typeof(Song).Name);

            bucket = new GridFSBucket(database, new GridFSBucketOptions()
            {
                BucketName = "OpusData",
                ChunkSizeBytes = 2097152
            });
        }

        internal async Task<Song> GetAsync(ObjectId Id)
        {
            var result = await collection.FindAsync((f) => f.Id.Equals(Id));
            return await result.FirstOrDefaultAsync();
        }

        internal async Task<Opus> GetStreamAsync(Song song)
        {
            song.LastAccessed = DateTime.Now;
            await collection.ReplaceOneAsync((f) => f.Id.Equals(song.Id), song);

            Opus stream = (Opus)song;
            stream.OpusStream = await bucket.OpenDownloadStreamAsync(song.OpusId);
            return stream;
        }
    }
}
