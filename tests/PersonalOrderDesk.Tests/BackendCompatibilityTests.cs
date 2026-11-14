using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Tests;
public sealed class BackendCompatibilityTests
{
    private static ServerInfo Info(string? version,string id="a")=>new("PersonalOrderDesk",1,id,"测试电脑","schema",version);
    [Fact]public void Matching_build_and_book_are_accepted()=>BackendCompatibility.Validate(Info("2.0.0-preview.2"),"2.0.0-preview.2","a");
    [Fact]public void Another_book_is_rejected_even_when_product_and_version_match()=>Assert.Throws<InvalidOperationException>(()=>BackendCompatibility.Validate(Info("2.0.0-preview.2","other"),"2.0.0-preview.2","a"));
    [Theory][InlineData(null)][InlineData("1.4.0")][InlineData("2.0.0-preview.1")]
    public void Old_backend_is_rejected_with_actionable_message(string? version){var error=Assert.Throws<InvalidOperationException>(()=>BackendCompatibility.Validate(Info(version),"2.0.0-preview.2"));Assert.Contains("退出旧后台",error.Message);Assert.Contains("无需重新建立",error.Message);}
}
