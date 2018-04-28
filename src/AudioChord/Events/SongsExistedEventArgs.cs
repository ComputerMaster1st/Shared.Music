﻿using System;

namespace AudioChord
{
    public class SongsExistedEventArgs : EventArgs
    {
        public ulong GuildId { get; }
        public ulong TextChannelId { get; }
        public int AlreadyInstalledSongsCount { get; }
        public int InstalledExistingSongsCount { get; }
        public int QueuedSongsCount { get; }
        public int FailedParsingSongsCount { get; }

        internal SongsExistedEventArgs(ulong guildId, ulong textChannelId, int alreadyInstalledSongsCount, int installedExistingSongsCount, int queuedSongsCount, int failedParsingSongsCount)
        {
            GuildId = guildId;
            TextChannelId = textChannelId;
            AlreadyInstalledSongsCount = alreadyInstalledSongsCount;
            InstalledExistingSongsCount = installedExistingSongsCount;
            QueuedSongsCount = queuedSongsCount;
            FailedParsingSongsCount = failedParsingSongsCount;
        }
    }
}