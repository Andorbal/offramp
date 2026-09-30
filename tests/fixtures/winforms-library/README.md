# winforms-library

A `net48` class library of Windows Forms controls, the shape most Windows Forms code bases
have (Open Live Writer keeps 15 of its 21 Windows Forms projects as libraries). The project is
a `library`, not `winforms`: kind detection gives `winforms` to applications only.

- `Editor.Controls/StatusControl.cs`: a `UserControl` that calls `SetStyle` and reads
  `DesignMode`, which exist on `net10.0-windows`, and builds a `ContextMenu` of `MenuItem`s,
  which .NET keeps only as binary-compatibility shims that throw (`[Obsolete]`, `WFDEV006`).

Exercised by: `audit api` (the project is compiled for `net10.0-windows` because it references
`System.Windows.Forms`; the shims are `OFR3003`).
