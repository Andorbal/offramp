' A Visual Basic helper the C# modules call, as DotNetNuke's WebUtility is.
Public NotInheritable Class ClientApi
    Private Sub New()
    End Sub

    Public Shared Function Escape(value As String) As String
        Return value.Replace("'", "\'")
    End Function
End Class
