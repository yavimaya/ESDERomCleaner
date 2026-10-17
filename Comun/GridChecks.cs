namespace ESDERomCleaner;

internal static class GridChecks
{
    // ¿Está el ratón sobre el dibujo de la casilla (13×13, centrado) de una celda de casillas? Con la columna ancha, un doble clic
    // junto a la casilla no la cambia por sí solo, así que el formulario lo trata como doble clic de la fila.
    public static bool OnGlyph(DataGridView grid, int column, int row)
    {
        var cell = grid.GetCellDisplayRectangle(column, row, false);
        var glyph = new Rectangle(cell.X + (cell.Width - 13) / 2, cell.Y + (cell.Height - 13) / 2, 13, 13);
        return glyph.Contains(grid.PointToClient(Cursor.Position));
    }
}
