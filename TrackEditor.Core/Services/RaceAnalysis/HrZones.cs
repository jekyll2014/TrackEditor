namespace TrackEditor.Core.Services.RaceAnalysis;

/// <summary>Defines the bpm bounds and label for one HR training zone.</summary>
public class HrZoneSpec
{
    public int Zone { get; set; }
    public string Name { get; set; } = "";
    public int LowerBpm { get; set; }
    public int UpperBpm { get; set; }

    public bool Contains(double hr) => hr >= LowerBpm && hr <= UpperBpm;
    public override string ToString() => $"Z{Zone} {Name} ({LowerBpm}–{UpperBpm} bpm)";

    /// <summary>Classic 5-zone schema from max HR (60/70/80/90% breakpoints).</summary>
    public static HrZoneSpec[] FromMaxHr(int maxHr) =>
    [
        new() { Zone=1, Name="Recovery",  LowerBpm=0,                   UpperBpm=(int)(maxHr*0.60) },
        new() { Zone=2, Name="Aerobic",   LowerBpm=(int)(maxHr*0.60)+1, UpperBpm=(int)(maxHr*0.70) },
        new() { Zone=3, Name="Tempo",     LowerBpm=(int)(maxHr*0.70)+1, UpperBpm=(int)(maxHr*0.80) },
        new() { Zone=4, Name="Threshold", LowerBpm=(int)(maxHr*0.80)+1, UpperBpm=(int)(maxHr*0.90) },
        new() { Zone=5, Name="VO2max",    LowerBpm=(int)(maxHr*0.90)+1, UpperBpm=maxHr+40          },
    ];

    /// <summary>Friel running zones from LTHR (Lactate Threshold HR).</summary>
    public static HrZoneSpec[] FromLthr(int lthr) =>
    [
        new() { Zone=1, Name="Recovery",  LowerBpm=0,                   UpperBpm=(int)(lthr*0.85)  },
        new() { Zone=2, Name="Aerobic",   LowerBpm=(int)(lthr*0.85)+1,  UpperBpm=(int)(lthr*0.89)  },
        new() { Zone=3, Name="Tempo",     LowerBpm=(int)(lthr*0.89)+1,  UpperBpm=(int)(lthr*0.94)  },
        new() { Zone=4, Name="Threshold", LowerBpm=(int)(lthr*0.94)+1,  UpperBpm=(int)(lthr*1.05)  },
        new() { Zone=5, Name="VO2max",    LowerBpm=(int)(lthr*1.05)+1,  UpperBpm=lthr+80           },
    ];
}
