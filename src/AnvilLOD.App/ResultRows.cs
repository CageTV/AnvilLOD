namespace AnvilLOD.App;

// Flat row types for the DataGrids. Kept separate from Core types so the UI can change freely.

public sealed record LevelRow(string Worldspace, int Lod4, int Lod8, int Lod16, int Lod32);

public sealed record BlockRow(string File, int Level, int X, int Y, int Refs, string Status);

public sealed record MissingRow(string Path, string Problem);
