namespace Agwinterm.Core;

[Flags]
public enum CellAttributes
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Inverse = 8,
    Dim = 16,
    Strikethrough = 32,
    Overline = 64,
}

public enum UnderlineStyle : byte { None, Single, Double, Curly, Dotted, Dashed }
