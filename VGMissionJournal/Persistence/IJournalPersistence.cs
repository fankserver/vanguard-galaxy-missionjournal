using System;

namespace VGMissionJournal.Persistence;

internal interface IJournalPersistence : IDisposable
{
    bool CanRecord { get; }
}
