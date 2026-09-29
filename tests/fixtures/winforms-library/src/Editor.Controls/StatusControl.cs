using System.Windows.Forms;

namespace Editor.Controls
{
    // A control in a class library, where Windows Forms code bases keep most of theirs. On
    // net10.0-windows SetStyle and DesignMode compile; ContextMenu and MenuItem compile only as
    // shims that throw at run time.
    public class StatusControl : UserControl
    {
        public StatusControl()
        {
            SetStyle(ControlStyles.UserPaint, true);
        }

        public bool Designing()
        {
            return DesignMode;
        }

        public ContextMenu Shortcuts()
        {
            var copy = new MenuItem("Copy");
            return new ContextMenu(new[] { copy });
        }
    }
}
