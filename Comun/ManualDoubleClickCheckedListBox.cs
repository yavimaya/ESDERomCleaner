namespace ESDERomCleaner;

internal sealed class ManualDoubleClickCheckedListBox : CheckedListBox
{
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private int pressedIndex = -1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WmLButtonDown or WmLButtonUp or WmLButtonDoubleClick)
        {
            int packed = m.LParam.ToInt32();
            int index = IndexFromPoint((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
            if (m.Msg == WmLButtonDown)
            {
                pressedIndex = index;
                Focus();
                if (index >= 0) SelectedIndex = index;
                return; // El primer clic solo enfoca la fila; nunca cambia la marca.
            }
            if (m.Msg == WmLButtonUp) return;
            if (index >= 0 && index == pressedIndex)
            {
                SetItemChecked(index, !GetItemChecked(index));
                OnDoubleClick(EventArgs.Empty);
            }
            pressedIndex = -1;
            return;
        }
        base.WndProc(ref m);
    }
}
