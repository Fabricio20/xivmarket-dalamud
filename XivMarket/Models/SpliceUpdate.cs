using System;

namespace XivMarket.Models;

public sealed record SpliceUpdate(
    int ItemId,
    int WorldId,
    string WorldName,
    bool IsHq,
    long Price,
    int Quantity,
    DateTimeOffset Timestamp);
