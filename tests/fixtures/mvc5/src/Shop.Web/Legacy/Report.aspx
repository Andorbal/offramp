<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Report.aspx.cs" Inherits="Shop.Web.Legacy.Report" %>
<%@ Register Src="~/Controls/Header.ascx" TagPrefix="shop" TagName="Header" %>
<!DOCTYPE html>
<html>
<body>
    <form id="form1" runat="server">
        <shop:Header runat="server" />
        <asp:Label ID="Total" runat="server" />
    </form>
</body>
</html>
