using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SaveHarbor {
 // This transport never contacts Baidu. Every credential below is explicitly fake.
 public class FakeBaidu : IBaiduTransport {
  class RemoteFile { public long Id; public string Path; public byte[] Bytes; }
  class PendingFile { public string Path; public Dictionary<int,byte[]> Parts=new Dictionary<int,byte[]>(); }
  Dictionary<string,RemoteFile> files=new Dictionary<string,RemoteFile>(); Dictionary<string,PendingFile> uploads=new Dictionary<string,PendingFile>();
  public HashSet<string> Dirs=new HashSet<string>(new[]{"/apps","/apps/Test"});
  long nextId=100; int nextUpload; public int ArchiveParts,Refreshes,Polls; public bool FailManifest,UnsafeDownload,UnsafeList,ExpireOnce,OmitRefresh;
  static byte[] Bytes(string s) { return Encoding.UTF8.GetBytes(s); }
  static string Q(string s) { return BaiduApi.Quote(s); }
  static Dictionary<string,string> Query(Uri uri) { var result=new Dictionary<string,string>(); foreach(var item in uri.Query.TrimStart('?').Split('&')) { if(item.Length==0) continue; var p=item.Split(new[]{'='},2); result[Uri.UnescapeDataString(p[0])]=p.Length>1?Uri.UnescapeDataString(p[1]):""; } return result; }
  static string Value(Dictionary<string,string> d,string key) { string v; return d.TryGetValue(key,out v)?v:""; }
  static string MD5(byte[] bytes) { using(var hash=System.Security.Cryptography.MD5.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
  public void BreakArchive(string path) { files[path].Bytes[0]^=255; }
  public byte[] Request(Uri uri,Dictionary<string,string> form,byte[] part,CancellationToken ct) {
   ct.ThrowIfCancellationRequested(); var query=Query(uri); var method=Value(query,"method");
   if(uri.AbsolutePath.EndsWith("/device/code")) return Bytes("{\"device_code\":\"TEST_DEVICE_NOT_REAL\",\"user_code\":\"TEST_CODE\",\"verification_url\":\"https://openapi.baidu.com/device\",\"qrcode_url\":\"https://openapi.baidu.com/device/qrcode/TEST\",\"expires_in\":600,\"interval\":1}");
   if(uri.AbsolutePath.EndsWith("/token")) {
    Tests.Check(Value(query,"client_secret")=="TEST_SECRET_NOT_REAL&+=","OAuth 参数编码"); var grant=Value(query,"grant_type");
    if(grant=="device_token"&&Polls++==0) return Bytes("{\"error\":\"authorization_pending\",\"error_description\":\"DO_NOT_EXPOSE_SERVER_TEXT\"}");
    if(grant=="refresh_token") Refreshes++;
    return Bytes("{\"access_token\":\"TEST_ACCESS_NOT_REAL\","+(OmitRefresh?"":"\"refresh_token\":\"TEST_REFRESH_NOT_REAL\",")+"\"expires_in\":3600}");
   }
   if(ExpireOnce) { ExpireOnce=false; return Bytes("{\"errno\":111}"); }
   if(method=="list") {
    var dir=Value(query,"dir"); if(!Dirs.Contains(dir)) return Bytes("{\"errno\":-9}");
    if(UnsafeList) return Bytes("{\"errno\":0,\"list\":[{\"fs_id\":100,\"server_filename\":\"bad\",\"path\":\"/elsewhere/bad\",\"size\":1,\"isdir\":0}]}");
    var children=new List<string>();
    foreach(var d in Dirs.Where(x=>x.StartsWith(dir+"/",StringComparison.Ordinal)&&x.Substring(dir.Length+1).IndexOf('/')<0)) children.Add("{\"fs_id\":42,\"path\":"+Q(d)+",\"server_filename\":"+Q(d.Substring(dir.Length+1))+",\"isdir\":1,\"size\":0}");
    foreach(var f in files.Values.Where(x=>x.Path.StartsWith(dir+"/",StringComparison.Ordinal)&&x.Path.Substring(dir.Length+1).IndexOf('/')<0)) children.Add("{\"fs_id\":"+f.Id+",\"path\":"+Q(f.Path)+",\"server_filename\":"+Q(f.Path.Substring(dir.Length+1))+",\"isdir\":0,\"size\":"+f.Bytes.Length+"}");
    int start,limit; int.TryParse(Value(query,"start"),out start); if(!int.TryParse(Value(query,"limit"),out limit)) limit=1000;
    return Bytes("{\"errno\":0,\"list\":["+string.Join(",",children.OrderBy(x=>x,StringComparer.Ordinal).Skip(start).Take(limit))+"]}");
   }
   if(method=="filemetas") {
    var id=J.Parse(Bytes(Value(query,"fsids"))).Items[0].Number; var f=files.Values.Single(x=>x.Id==id);
    return Bytes("{\"errno\":0,\"list\":[{\"fs_id\":"+id+",\"path\":"+Q(f.Path)+",\"size\":"+f.Bytes.Length+",\"dlink\":"+Q(UnsafeDownload?"https://attacker.invalid/file":"https://d.pcs.baidu.com/mock/"+id)+"}]}");
   }
   if(method=="precreate") {
    var path=Value(form,"path"); if(FailManifest&&path.EndsWith("/manifest.json",StringComparison.Ordinal)) throw new IOException("模拟清单提交失败");
    var count=J.Parse(Bytes(Value(form,"block_list"))).Items.Count; var id="TEST_UPLOAD_"+(nextUpload++).ToString(); uploads[id]=new PendingFile { Path=path };
    return Bytes("{\"errno\":0,\"return_type\":1,\"uploadid\":"+Q(id)+",\"block_list\":["+string.Join(",",Enumerable.Range(0,count))+"]}");
   }
   if(method=="upload") {
    var upload=uploads[Value(query,"uploadid")]; var index=int.Parse(Value(query,"partseq"),CultureInfo.InvariantCulture); upload.Parts[index]=part;
    if(upload.Path.EndsWith("/save.zip",StringComparison.Ordinal)) ArchiveParts++;
    Tests.Check(part.Length<=4*1024*1024,"分片不超过 4 MB"); return Bytes("{\"errno\":0,\"md5\":"+Q(MD5(part))+"}");
   }
   if(method=="create") {
    var path=Value(form,"path"); if(Value(form,"isdir")=="1") { if(Dirs.Contains(path)) return Bytes("{\"errno\":-8}"); Dirs.Add(path); return Bytes("{\"errno\":0,\"isdir\":1,\"path\":"+Q(path)+"}"); }
    var pending=uploads[Value(form,"uploadid")]; byte[] bytes;
    using(var output=new MemoryStream()) { foreach(var block in pending.Parts.OrderBy(x=>x.Key)) output.Write(block.Value,0,block.Value.Length); bytes=output.ToArray(); }
    Tests.Check(bytes.Length==long.Parse(Value(form,"size"),CultureInfo.InvariantCulture),"服务端组合大小");
    RemoteFile old; var id=files.TryGetValue(path,out old)?old.Id:nextId++; files[path]=new RemoteFile { Id=id,Path=path,Bytes=bytes };
    return Bytes("{\"errno\":0,\"fs_id\":"+id+",\"path\":"+Q(path)+",\"size\":"+bytes.Length+"}");
   }
   throw new Exception("未实现的模拟接口。");
  }
  public void Download(Uri uri,string destination,long expectedSize,CancellationToken ct,Action<long> progress) {
   ct.ThrowIfCancellationRequested(); Tests.Check(uri.Host=="d.pcs.baidu.com","下载主机"); var id=long.Parse(uri.Segments.Last(),CultureInfo.InvariantCulture); var f=files.Values.Single(x=>x.Id==id); Tests.Check(f.Bytes.Length==expectedSize,"下载预期大小"); File.WriteAllBytes(destination,f.Bytes); if(progress!=null) progress(f.Bytes.Length);
  }
 }
 public static class BaiduTests {
  static void Reject(Action action,string name) { var rejected=false; try { action(); } catch(InvalidDataException) { rejected=true; } catch(IOException) { rejected=true; } catch(InvalidOperationException) { rejected=true; } Tests.Check(rejected,name); }
  public static void Run(string root) {
   var folder=Path.Combine(root,"baidu-test"); Directory.CreateDirectory(folder); var data=Path.Combine(folder,"A"); var engine=new Engine(data); var vault=new BaiduVault(data);
   var config=new BaiduConfig { Enabled=true,AppKey="TEST_APP_NOT_REAL",SecretKey="TEST_SECRET_NOT_REAL&+=",CloudRoot="/apps/Test/SaveHarbor" }; config.Validate(true); vault.Save(config);
   Tests.Check(vault.Load().SecretKey==config.SecretKey,"加密配置重载"); Tests.Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(data,"baidu.settings.enc"))).Contains(config.SecretKey),"磁盘没有明文 Secret Key");
   Reject(()=>new BaiduConfig { CloudRoot="/apps/Test/../outside" }.Validate(false),"云端目录穿越拦截"); Reject(()=>BaiduHttp.Trusted(new Uri("https://attacker.invalid/file")),"非百度下载地址拦截"); Reject(()=>BaiduHttp.Trusted(new Uri("http://d.pcs.baidu.com/file")),"明文 HTTP 凭据地址拦截");
   var fake=new FakeBaidu(); var api=new BaiduApi(config,vault,fake,CancellationToken.None,null);
   var device=api.BeginDevice(); Tests.Check(device.Interval>=5,"授权轮询最小间隔"); Tests.Check(!api.PollDevice(device),"未授权等待"); Tests.Check(api.PollDevice(device),"设备授权完成");
   config.ExpiresAt=BaiduApi.Now-1; api.EnsureRoot(); Tests.Check(fake.Refreshes==1,"过期令牌自动刷新"); fake.ExpireOnce=true; api.TestConnection(); Tests.Check(fake.Refreshes==2,"接口过期错误后刷新并重试");
   var changed=config.Copy(); changed.AccessToken="TEST_OTHER_ACCOUNT_NOT_REAL"; changed.ClearStaleCredentials(config,config); Tests.Check(changed.RefreshToken=="","更换账号 Token 时清除旧刷新凭据");
   var reauthorized=config.Copy(); fake.OmitRefresh=true; new BaiduApi(reauthorized,null,fake,CancellationToken.None,null).ExchangeCode("TEST_CODE"); Tests.Check(reauthorized.RefreshToken=="","新授权不沿用旧账号刷新凭据"); fake.OmitRefresh=false;
   var saves=Path.Combine(folder,"saves"); Directory.CreateDirectory(saves); var content=new byte[12*1024*1024]; new Random(16).NextBytes(content); File.WriteAllBytes(Path.Combine(saves,"world.dat"),content);
   var game=new Game { Name="百度模拟游戏",Exe="SaveHarbor-Test-Game.exe",Rules=new List<Rule> { new Rule { Anchor="CustomRoot",Root=folder,Relative="saves" } } }; engine.State.Games.Add(game); engine.Persist();
   var first=engine.Backup(game,"模拟云端版本",""); var store=new BaiduStorage(config,api,Path.Combine(data,"baidu-cache")); engine.Publish(store,game,first,Path.Combine(engine.Folder(game.Id),first.Id)); Tests.Check(fake.ArchiveParts>=4,"大文件分片上传");
   var second=new Engine(Path.Combine(folder,"B")); Tests.Check(second.Fetch(store)==1,"百度后端完整上传拉取"); var imported=second.State.Games.Single(); Tests.Check(imported.Rules[0].Root==""&&imported.Exe=="","云端没有本机绑定");
   var newRoot=Path.Combine(folder,"new-root"); Directory.CreateDirectory(newRoot); imported.Rules[0].Root=newRoot; imported.Exe=game.Exe; second.Restore(imported,second.Versions(imported).Single()); Tests.Check(Engine.HashFile(Path.Combine(newRoot,"saves","world.dat"))==Engine.Hash(content),"模拟云端下载与跨设备恢复一致");
   File.WriteAllText(Path.Combine(saves,"slot.dat"),"next"); var next=engine.Backup(game,"未完成版本",first.Id); fake.FailManifest=true; Reject(()=>engine.Publish(store,game,next,Path.Combine(engine.Folder(game.Id),next.Id)),"清单上传失败"); fake.FailManifest=false; Tests.Check(store.Versions(game).Count()==1,"不导入半完成云端版本");
   using(var cancel=new CancellationTokenSource()) {
    var canceledApi=new BaiduApi(config,vault,fake,cancel.Token,delegate(string message) { cancel.Cancel(); }); var canceledStore=new BaiduStorage(config,canceledApi,Path.Combine(data,"cancel-cache")); var rejected=false;
    try { canceledStore.Publish(game,next,Path.Combine(engine.Folder(game.Id),next.Id)); } catch(OperationCanceledException) { rejected=true; } Tests.Check(rejected,"取消中断上传");
   }
   Tests.Check(store.Versions(game).Count()==1,"取消上传不提交清单");
   fake.UnsafeDownload=true; Reject(()=>second.Fetch(store),"下载链接域名拦截"); fake.UnsafeDownload=false; fake.UnsafeList=true; Reject(()=>api.List(config.CloudRoot),"云端文件路径越界拦截"); fake.UnsafeList=false;
   fake.Dirs.Add("/apps/Test/pagination"); for(var i=0;i<1001;i++) fake.Dirs.Add("/apps/Test/pagination/d"+i.ToString("D4")); Tests.Check(api.List("/apps/Test/pagination").Count==1001,"超过 1000 条目的分页");
   fake.BreakArchive(config.CloudRoot+"/"+game.Id+"/"+first.Id+"/save.zip"); var third=new Engine(Path.Combine(folder,"C")); Reject(()=>third.Fetch(store),"云端损坏包 SHA-256 拦截");
   var sanitized=false; try { BaiduApi.Check(J.Parse(Encoding.UTF8.GetBytes("{\"error\":\"invalid_client\",\"error_description\":\"TEST_SECRET_NOT_REAL\"}"))); } catch(BaiduError ex) { sanitized=!ex.Message.Contains("TEST_SECRET_NOT_REAL"); } Tests.Check(sanitized,"错误信息不显示服务端敏感正文");
   File.WriteAllText(Path.Combine(root,"BAIDU-PASSED.txt"),"PASS: DPAPI config; encrypted credential storage; OAuth parameter encoding; device-code pending/success; minimum poll interval; proactive/reactive token refresh; 4 MB slices; 12 MB archive upload/download/restore; portable cloud profile; incomplete publish rejection; cancellation; trusted HTTPS download hosts; listing path validation; 1001-item pagination; corrupted cloud archive rejection; redacted API errors. Real Baidu account integration is NOT tested.");
  }
 }
}
