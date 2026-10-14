using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace SaveHarbor {
 [DataContract] public class BaiduConfig {
  [DataMember] public bool Enabled;
  [DataMember] public string AppKey="";
  [DataMember] public string SecretKey="";
  [DataMember] public string Scope="basic,netdisk";
  [DataMember] public string RedirectUri="oob";
  [DataMember] public string CloudRoot="/apps/你的应用名称/SaveHarbor";
  [DataMember] public string AccessToken="";
  [DataMember] public string RefreshToken="";
  [DataMember] public long ExpiresAt;
  [DataMember] public int TimeoutSeconds=120;
  [DataMember] public int MaxArchiveMB=4096;
  public BaiduConfig Copy() { return Json.Decode<BaiduConfig>(Json.Encode(this)); }
  public void ClearStaleCredentials(BaiduConfig original,BaiduConfig tokenState) {
   if(AppKey!=original.AppKey||SecretKey!=original.SecretKey||Scope!=original.Scope) { if(AccessToken==original.AccessToken) AccessToken=""; if(RefreshToken==original.RefreshToken) RefreshToken=""; ExpiresAt=0; }
   if(AccessToken!=tokenState.AccessToken) { ExpiresAt=0; if(RefreshToken==tokenState.RefreshToken) RefreshToken=""; }
  }
  public void Validate(bool authorize) {
   if(authorize&&(string.IsNullOrWhiteSpace(AppKey)||string.IsNullOrWhiteSpace(SecretKey))) throw new InvalidOperationException("授权需要 AppKey / Client ID 和 Secret Key。");
   if(TimeoutSeconds<10||TimeoutSeconds>600) throw new InvalidOperationException("请求超时应为 10–600 秒。");
   if(MaxArchiveMB<1||MaxArchiveMB>131072) throw new InvalidOperationException("单包上限应为 1–131072 MB。");
   CloudRoot=RemotePath(CloudRoot);
   if(!CloudRoot.StartsWith("/apps/",StringComparison.Ordinal)||CloudRoot.Split('/').Length<3) throw new InvalidOperationException("云端目录应在 /apps/应用名称/ 下。");
   if(string.IsNullOrWhiteSpace(Scope)||!Scope.Split(',').Select(x=>x.Trim()).Contains("netdisk")) throw new InvalidOperationException("Scope 必须包含 netdisk。");
   if(RedirectUri!="oob") { Uri uri; if(!Uri.TryCreate(RedirectUri,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo.Length>0) throw new InvalidOperationException("回调地址应为 oob 或开发者配置的 HTTPS 地址。"); }
  }
  public static string RemotePath(string input) {
   var p=(input??"").Trim().TrimEnd('/');
   if(!p.StartsWith("/",StringComparison.Ordinal)||p.Contains("\\")||p.IndexOfAny(new[]{'?','#',':','*','"','<','>','|','\0','\r','\n'})>=0||p.Split('/').Skip(1).Any(x=>x.Length==0||x=="."||x=="..")||p.Length>1024) throw new InvalidDataException("云端目录格式无效。");
   return p;
  }
 }
 public class BaiduVault {
  readonly string file;
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("SaveHarbor/Baidu/v1");
  public BaiduVault(string data) { file=Path.Combine(data,"baidu.settings.enc"); }
  public BaiduConfig Load() {
   if(!File.Exists(file)) return new BaiduConfig();
   try { return Json.Decode<BaiduConfig>(ProtectedData.Unprotect(File.ReadAllBytes(file),Entropy,DataProtectionScope.CurrentUser)); }
   catch(Exception) { throw new IOException("百度配置无法解密。请使用原 Windows 用户，或先保留配置文件再重新配置。"); }
  }
  public void Save(BaiduConfig config) {
   var bytes=ProtectedData.Protect(Json.Encode(config),Entropy,DataProtectionScope.CurrentUser);
   Directory.CreateDirectory(Path.GetDirectoryName(file)); var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
   try { File.WriteAllBytes(temp,bytes); if(File.Exists(file)) File.Replace(temp,file,null); else File.Move(temp,file); }
   finally { if(File.Exists(temp)) File.Delete(temp); }
  }
 }
 public class J {
  readonly XElement node;
  J(XElement n) { node=n; }
  public static J Parse(byte[] bytes) { try { using(var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,XmlDictionaryReaderQuotas.Max)) return new J(XElement.Load(reader)); } catch { throw new InvalidDataException("百度返回了无法解析的 JSON。"); } }
  public J At(string name) { return new J(node==null?null:node.Element(name)); }
  public bool Exists { get { return node!=null; } }
  public string Text { get { return node==null?"":node.Value; } }
  public long Number { get { if(node==null) return 0; long n; if(!long.TryParse(Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out n)) throw new InvalidDataException("接口响应的数字字段无效。"); return n; } }
  public List<J> Items { get { return node==null?new List<J>():node.Elements().Select(x=>new J(x)).ToList(); } }
 }
 public class BaiduError : IOException {
  public string Code;
  public BaiduError(string code) : base(MessageFor(code)) { Code=code; }
  static string MessageFor(string code) {
   switch(code) {
    case "authorization_pending": return "等待扫码授权。";
    case "slow_down": return "授权轮询过快，已延长等待间隔。";
    case "invalid_client": return "应用凭据无效，请检查 AppKey / Secret Key。";
    case "invalid_grant": return "授权码或刷新凭据失效，请重新授权。";
    case "authorization_declined": case "access_denied": return "用户拒绝了授权。";
    case "110": case "111": case "-6": return "百度授权已失效，请重新授权或填写有效 Token。";
    case "-9": return "云端路径不存在，请检查应用目录或先上传版本。";
    case "6": return "应用没有该接口的权限，请检查开放平台配置。";
    case "4": case "18": return "请求超过接口频率限制，请稍后重试。";
    default: return "百度接口返回错误码 "+code+"。请检查应用权限、目录或稍后重试。";
   }
  }
 }
 public interface IBaiduTransport {
  byte[] Request(Uri uri,Dictionary<string,string> form,byte[] part,CancellationToken ct);
  void Download(Uri uri,string destination,long expectedSize,CancellationToken ct,Action<long> progress);
 }
 public class BaiduHttp : IBaiduTransport {
  readonly int timeout;
  public BaiduHttp(int seconds) { timeout=seconds*1000; ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12; }
  public static Uri Trusted(Uri uri) {
   if(uri.Scheme!="https"||uri.UserInfo.Length>0||!uri.IsDefaultPort||!(uri.Host=="baidu.com"||uri.Host.EndsWith(".baidu.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".baidupcs.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".bcebos.com",StringComparison.OrdinalIgnoreCase))) throw new IOException("百度返回了非受信任的 HTTPS 地址，已停止传输。");
   return uri;
  }
  HttpWebRequest Create(Uri uri) { var r=(HttpWebRequest)WebRequest.Create(Trusted(uri)); r.Timeout=timeout; r.ReadWriteTimeout=timeout; r.AllowAutoRedirect=false; r.UserAgent="pan.baidu.com SaveHarbor/0.2"; return r; }
  public byte[] Request(Uri uri,Dictionary<string,string> form,byte[] part,CancellationToken ct) {
   var r=Create(uri); byte[] body=null;
   if(part!=null) {
    var boundary="SaveHarbor"+Guid.NewGuid().ToString("N"); r.ContentType="multipart/form-data; boundary="+boundary;
    var begin=Encoding.ASCII.GetBytes("--"+boundary+"\r\nContent-Disposition: form-data; name=\"file\"; filename=\"part\"\r\nContent-Type: application/octet-stream\r\n\r\n"); var end=Encoding.ASCII.GetBytes("\r\n--"+boundary+"--\r\n");
    using(var buffer=new MemoryStream()) { buffer.Write(begin,0,begin.Length); buffer.Write(part,0,part.Length); buffer.Write(end,0,end.Length); body=buffer.ToArray(); }
   } else if(form!=null) { r.ContentType="application/x-www-form-urlencoded"; body=Encoding.UTF8.GetBytes(BaiduApi.Query(form)); }
   if(body!=null) { r.Method="POST"; r.ContentLength=body.Length; }
   using(ct.Register(()=>r.Abort())) try {
    ct.ThrowIfCancellationRequested(); if(body!=null) using(var output=r.GetRequestStream()) output.Write(body,0,body.Length);
    using(var response=(HttpWebResponse)r.GetResponse()) using(var input=response.GetResponseStream()) using(var result=new MemoryStream()) {
     var block=new byte[8192]; int count; while((count=input.Read(block,0,block.Length))>0) { ct.ThrowIfCancellationRequested(); if(result.Length+count>8*1024*1024) throw new IOException("接口响应超过大小限制。"); result.Write(block,0,count); } return result.ToArray();
    }
   } catch(WebException ex) {
    ct.ThrowIfCancellationRequested(); var response=ex.Response as HttpWebResponse;
    if(response!=null) using(response) {
     using(var input=response.GetResponseStream()) using(var buffer=new MemoryStream()) {
      var bytes=new byte[8192]; int n; while(buffer.Length<65536&&(n=input.Read(bytes,0,bytes.Length))>0) buffer.Write(bytes,0,n);
      if(buffer.Length>0) { J error=null; try { error=J.Parse(buffer.ToArray()); } catch(InvalidDataException) {} if(error!=null) BaiduApi.Check(error); }
     }
     throw new IOException("百度请求失败（HTTP "+(int)response.StatusCode+"）。");
    }
    throw new IOException("百度请求未完成。请检查网络或超时设置。");
   }
  }
  public void Download(Uri uri,string destination,long expectedSize,CancellationToken ct,Action<long> progress) {
   for(var redirect=0;redirect<6;redirect++) {
    var r=Create(uri);
    using(ct.Register(()=>r.Abort())) try {
     ct.ThrowIfCancellationRequested(); using(var response=(HttpWebResponse)r.GetResponse()) {
      if((int)response.StatusCode>=300&&(int)response.StatusCode<400) { var target=response.Headers["Location"]; if(string.IsNullOrEmpty(target)) throw new IOException("下载重定向缺少目标。"); uri=Trusted(new Uri(uri,target)); continue; }
      if(response.ContentLength>=0&&response.ContentLength!=expectedSize) throw new IOException("下载大小与元信息不一致。");
      using(var input=response.GetResponseStream()) using(var output=File.Create(destination)) { var buffer=new byte[65536]; long total=0; int n; while((n=input.Read(buffer,0,buffer.Length))>0) { ct.ThrowIfCancellationRequested(); total+=n; if(total>expectedSize) throw new IOException("下载超过预期大小。"); output.Write(buffer,0,n); if(progress!=null) progress(total); } if(total!=expectedSize) throw new IOException("下载不完整。"); } return;
     }
    } catch(WebException) { ct.ThrowIfCancellationRequested(); throw new IOException("百度下载失败。请重试或检查授权与网络。"); }
   }
   throw new IOException("下载重定向次数过多。");
  }
 }
 public class DeviceCode {
  public string Code,UserCode,Url,QrUrl;
  public int Expires,Interval;
 }
 public class BaiduEntry { public string Name,Path; public long Id,Size; public bool Directory; }
 public class BaiduApi {
  readonly BaiduConfig config; readonly BaiduVault vault; readonly IBaiduTransport transport; readonly CancellationToken ct; readonly Action<string> report;
  public BaiduApi(BaiduConfig c,BaiduVault v,IBaiduTransport t,CancellationToken token,Action<string> progress) { config=c; vault=v; transport=t??new BaiduHttp(c.TimeoutSeconds); ct=token; report=progress??delegate {}; }
  public static long Now { get { return (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds; } }
  public static string Query(Dictionary<string,string> values) { return string.Join("&",values.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value??""))); }
  public static string Quote(string value) { return Encoding.UTF8.GetString(Json.Encode(value)); }
  public static Dictionary<string,string> Params(params string[] pairs) { var d=new Dictionary<string,string>(); for(var i=0;i<pairs.Length;i+=2) d[pairs[i]]=pairs[i+1]; return d; }
  public static void Check(J json) {
   if(json.At("error").Exists&&json.At("error").Text.Length>0) throw new BaiduError(json.At("error").Text);
   if(json.At("errno").Exists&&json.At("errno").Number!=0) throw new BaiduError(json.At("errno").Text);
   if(json.At("error_code").Exists&&json.At("error_code").Number!=0) throw new BaiduError(json.At("error_code").Text);
  }
  J OAuth(string action,Dictionary<string,string> query) { var j=J.Parse(transport.Request(new Uri("https://openapi.baidu.com/oauth/2.0/"+action+"?"+Query(query)),null,null,ct)); Check(j); return j; }
  void TakeToken(J response,bool refreshing=false) {
   var access=response.At("access_token").Text; if(access.Length==0) throw new InvalidDataException("授权响应缺少 Access Token。");
   var refresh=response.At("refresh_token").Text;
   ct.ThrowIfCancellationRequested(); config.AccessToken=access; if(refresh.Length>0||!refreshing) config.RefreshToken=refresh;
   config.ExpiresAt=response.At("expires_in").Number>0?Now+response.At("expires_in").Number:0; if(vault!=null) vault.Save(config);
  }
  public DeviceCode BeginDevice() {
   config.Validate(true); var j=OAuth("device/code",Params("response_type","device_code","client_id",config.AppKey,"scope",config.Scope));
   if(j.At("device_code").Text.Length==0) throw new InvalidDataException("未取得设备授权码。");
   return new DeviceCode { Code=j.At("device_code").Text,UserCode=j.At("user_code").Text,Url=j.At("verification_url").Text,QrUrl=j.At("qrcode_url").Text,Expires=(int)j.At("expires_in").Number,Interval=Math.Max(5,(int)j.At("interval").Number) };
  }
  public bool PollDevice(DeviceCode device) { try { TakeToken(OAuth("token",Params("grant_type","device_token","code",device.Code,"client_id",config.AppKey,"client_secret",config.SecretKey))); return true; } catch(BaiduError e) { if(e.Code=="authorization_pending") return false; if(e.Code=="slow_down") { device.Interval+=5; return false; } throw; } }
  public void ExchangeCode(string code) { config.Validate(true); TakeToken(OAuth("token",Params("grant_type","authorization_code","code",code.Trim(),"client_id",config.AppKey,"client_secret",config.SecretKey,"redirect_uri",config.RedirectUri))); }
  public void Refresh() { config.Validate(true); if(config.RefreshToken.Length==0) throw new InvalidOperationException("没有 Refresh Token，请重新授权。"); TakeToken(OAuth("token",Params("grant_type","refresh_token","refresh_token",config.RefreshToken,"client_id",config.AppKey,"client_secret",config.SecretKey)),true); }
  public static Uri AuthLink(string link) { Uri u; if(!Uri.TryCreate(link,UriKind.Absolute,out u)||u.Host!="openapi.baidu.com"||u.UserInfo.Length>0||!u.IsDefaultPort||(u.Scheme!="https"&&u.Scheme!="http")) throw new IOException("设备授权地址无效。"); return new UriBuilder(u) { Scheme="https",Port=-1 }.Uri; }
  public byte[] QR(DeviceCode d) { return transport.Request(AuthLink(d.QrUrl),null,null,ct); }
  public string AuthorizationUrl(string state) { config.Validate(true); return "https://openapi.baidu.com/oauth/2.0/authorize?"+Query(Params("response_type","code","client_id",config.AppKey,"redirect_uri",config.RedirectUri,"scope",config.Scope,"state",state,"display","tv","qrcode","1")); }
  string Token() { config.Validate(false); if(config.AccessToken.Length==0) throw new InvalidOperationException("请先打开百度配置完成授权，或填写有效 Access Token。"); if(config.ExpiresAt>0&&config.ExpiresAt<=Now+60) Refresh(); return config.AccessToken; }
  J Api(string endpoint,Dictionary<string,string> query,Dictionary<string,string> form,byte[] part) {
   for(var attempt=0;attempt<2;attempt++) {
    query["access_token"]=Token(); var host=endpoint=="slice"?"https://d.pcs.baidu.com/rest/2.0/pcs/superfile2":endpoint=="meta"?"https://pan.baidu.com/rest/2.0/xpan/multimedia":"https://pan.baidu.com/rest/2.0/xpan/file";
    try { var j=J.Parse(transport.Request(new Uri(host+"?"+Query(query)),form,part,ct)); Check(j); return j; }
    catch(BaiduError ex) { if(attempt==0&&(ex.Code=="110"||ex.Code=="111"||ex.Code=="-6")&&config.RefreshToken.Length>0) { Refresh(); continue; } throw; }
   }
   throw new IOException("授权刷新后接口仍失败。");
  }
  public List<BaiduEntry> List(string dir) {
   var files=new List<BaiduEntry>(); for(var start=0;;start+=1000) {
    ct.ThrowIfCancellationRequested(); var j=Api("file",Params("method","list","dir",dir,"start",start.ToString(CultureInfo.InvariantCulture),"limit","1000","order","name"),null,null);
    if(!j.At("list").Exists) throw new InvalidDataException("文件列表响应缺少 list。");
    var page=j.At("list").Items; foreach(var item in page) {
     var name=item.At("server_filename").Text; if(name.Length==0||name.Contains("/")||name.Contains("\\")||name=="."||name=="..") throw new InvalidDataException("云端条目名称无效。");
     var p=dir.TrimEnd('/')+"/"+name; if(item.At("path").Text!=p||item.At("fs_id").Number<=0||item.At("size").Number<0) throw new InvalidDataException("云端条目元信息无效。");
     files.Add(new BaiduEntry { Name=name,Path=p,Id=item.At("fs_id").Number,Size=item.At("size").Number,Directory=item.At("isdir").Number==1 });
    }
    if(page.Count<1000) return files; if(start>=1000000) throw new IOException("目录条目数超过安全上限。");
   }
  }
  public void Mkdir(string path) {
   BaiduConfig.RemotePath(path);
   try { Api("file",Params("method","create"),Params("path",path,"isdir","1","rtype","0"),null); }
   catch(BaiduError e) { if(e.Code!="-8") throw; var parent=path.Substring(0,path.LastIndexOf('/')); if(parent.Length==0) parent="/"; if(!List(parent).Any(x=>x.Path==path&&x.Directory)) throw; }
  }
  public void EnsureRoot() {
   config.Validate(false); var parts=config.CloudRoot.Split('/'); var current="/apps/"+parts[2];
   Mkdir(current);
   for(var i=3;i<parts.Length;i++) { current+="/"+parts[i]; Mkdir(current); }
  }
  static string MD5Of(byte[] bytes) { using(var md5=MD5.Create()) return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
  static int ReadBlock(Stream input,byte[] buffer) { var total=0; int n; while(total<buffer.Length&&(n=input.Read(buffer,total,buffer.Length-total))>0) total+=n; return total; }
  public void Upload(string path,string local,bool replace) {
   ct.ThrowIfCancellationRequested(); var size=new FileInfo(local).Length; if(size==0||size>(long)config.MaxArchiveMB*1024*1024) throw new IOException("文件为空或超过配置的单包上限。");
   var hashes=new List<string>(); var buffer=new byte[4*1024*1024];
   using(var input=File.OpenRead(local)) { int n; while((n=ReadBlock(input,buffer))>0) { ct.ThrowIfCancellationRequested(); var block=new byte[n]; Buffer.BlockCopy(buffer,0,block,0,n); hashes.Add(MD5Of(block)); } }
   var list=Encoding.UTF8.GetString(Json.Encode(hashes)); var pre=Api("file",Params("method","precreate"),Params("path",path,"size",size.ToString(CultureInfo.InvariantCulture),"isdir","0","autoinit","1","rtype",replace?"2":"0","block_list",list),null);
   if(pre.At("return_type").Number==2) { report("秒传完成："+Path.GetFileName(path)); return; }
   var uploadId=pre.At("uploadid").Text; if(uploadId.Length==0||!pre.At("block_list").Exists) throw new InvalidDataException("预上传响应不完整。");
   using(var input=File.OpenRead(local)) foreach(var part in pre.At("block_list").Items) {
    var index=(int)part.Number; if(index<0||index>=hashes.Count) throw new InvalidDataException("上传分片编号无效。"); input.Position=(long)index*buffer.Length; var n=ReadBlock(input,buffer); var bytes=new byte[n]; Buffer.BlockCopy(buffer,0,bytes,0,n);
    if(MD5Of(bytes)!=hashes[index]) throw new IOException("上传中的文件发生变化。");
    var response=Api("slice",Params("method","upload","type","tmpfile","path",path,"uploadid",uploadId,"partseq",index.ToString(CultureInfo.InvariantCulture)),null,bytes);
    if(response.At("md5").Text!=hashes[index]) throw new InvalidDataException("服务端分片校验不一致。"); report("上传 "+Path.GetFileName(path)+" · 分片 "+(index+1)+" / "+hashes.Count);
   }
   var created=Api("file",Params("method","create"),Params("path",path,"size",size.ToString(CultureInfo.InvariantCulture),"isdir","0","uploadid",uploadId,"rtype",replace?"2":"0","block_list",list),null);
   if(created.At("path").Text!=path||created.At("size").Number!=size) throw new InvalidDataException("服务端创建文件结果不一致。");
  }
  public void Get(BaiduEntry entry,string target,long maxSize) {
   if(entry.Size>maxSize) throw new IOException("云端文件超过配置的大小限制。");
   var meta=Api("meta",Params("method","filemetas","dlink","1","fsids","["+entry.Id.ToString(CultureInfo.InvariantCulture)+"]"),null,null).At("list").Items.SingleOrDefault();
   if(meta==null||meta.At("fs_id").Number!=entry.Id||meta.At("size").Number!=entry.Size||(meta.At("path").Exists&&meta.At("path").Text!=entry.Path)) throw new InvalidDataException("下载元信息不一致。");
   var dlink=BaiduHttp.Trusted(new Uri(meta.At("dlink").Text)); var query=string.Join("&",dlink.Query.TrimStart('?').Split('&').Where(x=>x.Length>0&&Uri.UnescapeDataString(x.Split('=')[0])!="access_token")); var url=new UriBuilder(dlink) { Fragment="",Query=(query.Length>0?query+"&":"")+"access_token="+Uri.EscapeDataString(Token()) }.Uri;
   transport.Download(url,target,entry.Size,ct,delegate(long done) { report("下载 "+entry.Name+" · "+MainWindow.Size(done)+" / "+MainWindow.Size(entry.Size)); });
  }
  public string TestConnection() { var j=Api("file",Params("method","list","dir",config.CloudRoot,"start","0","limit","1"),null,null); if(!j.At("list").Exists) throw new InvalidDataException("目录列表响应不完整。"); return "百度连接成功，应用目录可读取。云端当前返回 "+j.At("list").Items.Count+" 个条目。"; }
 }
 public class BaiduStorage : IStorageProvider {
  readonly BaiduConfig config; readonly BaiduApi api; readonly string cache;
  public BaiduStorage(BaiduConfig c,BaiduApi a,string cacheRoot) { config=c; api=a; cache=cacheRoot; Directory.CreateDirectory(cache); }
  static string Id(string id) { Guid v; if(!Guid.TryParseExact(id,"N",out v)) throw new InvalidDataException("云端档案标识无效。"); return id; }
  string GamePath(string id) { return config.CloudRoot+"/"+Id(id); }
  string VersionPath(Snapshot s) { return GamePath(s.GameId)+"/"+Id(s.Id); }
  List<BaiduEntry> Entries(string dir) { return api.List(dir); }
  T Read<T>(BaiduEntry entry) {
   var temp=Path.Combine(cache,Guid.NewGuid().ToString("N")+".json"); try { api.Get(entry,temp,4*1024*1024); return Json.Read<T>(temp); } finally { if(File.Exists(temp)) File.Delete(temp); }
  }
  public void Publish(Game g,Snapshot s,string local) {
   if(s.GameId!=g.Id) throw new InvalidDataException("游戏与版本不匹配。");
   if(new FileInfo(Path.Combine(local,"manifest.json")).Length>4*1024*1024||Json.Encode(g.Portable()).Length>4*1024*1024) throw new IOException("版本元数据超过 4 MB，请缩小存档范围后重试。");
   api.EnsureRoot(); var gameDir=GamePath(g.Id); api.Mkdir(gameDir); var dir=VersionPath(s); api.Mkdir(dir);
   var contents=Entries(dir); var committed=contents.FirstOrDefault(x=>x.Name=="manifest.json"&&!x.Directory);
   if(committed!=null) {
    var old=Read<Snapshot>(committed); if(old.Id!=s.Id||old.GameId!=s.GameId||old.ZipHash!=s.ZipHash||old.Fingerprint!=s.Fingerprint) throw new IOException("同名云端版本的内容不同，已停止覆盖。");
    var zip=contents.SingleOrDefault(x=>x.Name=="save.zip"&&!x.Directory); if(zip==null||zip.Size!=new FileInfo(Path.Combine(local,"save.zip")).Length) throw new IOException("云端已提交版本缺少完整备份包。");
   } else {
    api.Upload(dir+"/save.zip",Path.Combine(local,"save.zip"),true);
    // Commit marker is uploaded only after every archive slice is accepted.
    api.Upload(dir+"/manifest.json",Path.Combine(local,"manifest.json"),true);
   }
   var profile=Path.Combine(cache,Guid.NewGuid().ToString("N")+".json"); try { Json.Write(profile,g.Portable()); api.Upload(gameDir+"/game.json",profile,true); } finally { if(File.Exists(profile)) File.Delete(profile); }
  }
  public IEnumerable<Game> Games() {
   foreach(var dir in Entries(config.CloudRoot).Where(x=>x.Directory)) {
    Guid id; if(!Guid.TryParseExact(dir.Name,"N",out id)) continue;
    var profile=Entries(dir.Path).SingleOrDefault(x=>x.Name=="game.json"&&!x.Directory); if(profile==null) continue;
    var game=Read<Game>(profile); if(game.Id!=dir.Name) throw new InvalidDataException("云端游戏档案目录不一致。"); yield return game.Portable();
   }
  }
  public IEnumerable<Snapshot> Versions(Game g) {
   foreach(var dir in Entries(GamePath(g.Id)).Where(x=>x.Directory)) {
    Guid id; if(!Guid.TryParseExact(dir.Name,"N",out id)) continue;
    var contents=Entries(dir.Path); var manifest=contents.SingleOrDefault(x=>x.Name=="manifest.json"&&!x.Directory); if(manifest==null) continue;
    if(!contents.Any(x=>x.Name=="save.zip"&&!x.Directory)) throw new InvalidDataException("云端版本已提交但备份包缺失。");
    var s=Read<Snapshot>(manifest); if(s.Id!=dir.Name||s.GameId!=g.Id) throw new InvalidDataException("云端版本标识不一致。"); yield return s;
   }
  }
  public void Download(Snapshot s,string local) { var file=Entries(VersionPath(s)).SingleOrDefault(x=>x.Name=="save.zip"&&!x.Directory); if(file==null) throw new IOException("云端版本没有备份包。"); api.Get(file,Path.Combine(local,"save.zip"),(long)config.MaxArchiveMB*1024*1024); }
 }
 public class BaiduAuthWindow : Window {
  CancellationTokenSource cancel=new CancellationTokenSource(); TextBlock text=new TextBlock { Text="正在请求设备码…",TextWrapping=TextWrapping.Wrap }; Image qr=new Image { Width=230,Height=230 }; Button link=new Button { Content="打开授权网页",Padding=new Thickness(12),IsEnabled=false }; BaiduConfig config; BaiduVault vault;
  public BaiduAuthWindow(BaiduConfig c,BaiduVault v) {
   config=c; vault=v; Title="百度网盘扫码授权"; Width=440; Height=470; WindowStartupLocation=WindowStartupLocation.CenterOwner; ResizeMode=ResizeMode.NoResize;
   var panel=new StackPanel { Margin=new Thickness(24) }; Content=panel; panel.Children.Add(text); panel.Children.Add(qr); panel.Children.Add(link); var stop=new Button { Content="取消",Margin=new Thickness(0,10,0,0),Padding=new Thickness(10) }; stop.Click+=delegate { Close(); }; panel.Children.Add(stop); Closed+=delegate { cancel.Cancel(); }; Loaded+=Begin;
  }
  async void Begin(object sender,RoutedEventArgs args) {
   var ct=cancel.Token;
   try {
    var api=new BaiduApi(config,null,null,ct,null); var device=await Task.Run(()=>api.BeginDevice()); ct.ThrowIfCancellationRequested();
    var expires=BaiduApi.Now+Math.Min(1800,device.Expires>0?device.Expires:600);
    text.Text="用百度网盘扫描二维码，或打开网页输入设备码："+device.UserCode;
    var url=BaiduApi.AuthLink(device.Url); link.IsEnabled=true; link.Click+=delegate { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute=true }); };
    try { var image=await Task.Run(()=>api.QR(device)); ct.ThrowIfCancellationRequested(); using(var stream=new MemoryStream(image)) { var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.StreamSource=stream; bitmap.EndInit(); bitmap.Freeze(); qr.Source=bitmap; } } catch(OperationCanceledException) { throw; } catch { text.Text+="\n二维码加载失败，可点击下方网页完成授权。"; }
    while(BaiduApi.Now<expires) { await Task.Delay(device.Interval*1000,ct); if(await Task.Run(()=>api.PollDevice(device))) { ct.ThrowIfCancellationRequested(); DialogResult=true; return; } }
    text.Text="设备码已过期。关闭窗口后重新开始授权。";
   } catch(OperationCanceledException) {} catch(Exception ex) { if(!ct.IsCancellationRequested) text.Text=ex.Message; }
  }
 }
 public class BaiduSettingsWindow : Window {
  public BaiduConfig Result; BaiduConfig original; BaiduVault vault; CheckBox enabled=new CheckBox { Content="使用百度网盘作为存档库",Margin=new Thickness(0,0,0,16) };
  TextBox key,scope,redirect,root,timeout,max; PasswordBox secret,access,refresh; TextBlock status=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12) }; StackPanel body; bool busy; CancellationTokenSource operation;
  public BaiduSettingsWindow(BaiduConfig config,BaiduVault storage) {
   original=config.Copy(); Result=config.Copy(); vault=storage; Title="百度网盘配置 · 本机加密保存"; Width=760; Height=820; MinHeight=600; Background=Brushes.White; Foreground=Brushes.Black; FontFamily=new FontFamily("Microsoft YaHei UI"); FontSize=14; WindowStartupLocation=WindowStartupLocation.CenterOwner;
   body=new StackPanel { Margin=new Thickness(24) }; Content=new ScrollViewer { Content=body }; body.Children.Add(enabled); enabled.IsChecked=config.Enabled;
   key=Field("AppKey / Client ID",config.AppKey); secret=Secret("Secret Key",config.SecretKey); root=Field("云端目录（必须属于你应用的 /apps/应用名称/）",config.CloudRoot); scope=Field("Scope",config.Scope); redirect=Field("授权码模式回调地址（设备码模式不需要回调）",config.RedirectUri);
   var advanced=new Expander { Header="高级参数 / 手动 Token",Margin=new Thickness(0,8,0,12) }; var extras=new StackPanel(); advanced.Content=extras; body.Children.Add(advanced);
   access=Secret("Access Token（可选，手动填入）",config.AccessToken,extras); refresh=Secret("Refresh Token（可选）",config.RefreshToken,extras); timeout=Field("每次请求超时（秒）",config.TimeoutSeconds.ToString(),extras); max=Field("单个文件包上限（MB）",config.MaxArchiveMB.ToString(),extras);
   var actions=new WrapPanel(); body.Children.Add(actions);
   Add(actions,"扫码授权",delegate { var c=Read(); c.Validate(true); var auth=new BaiduAuthWindow(c,vault) { Owner=this }; if(auth.ShowDialog()==true) { Result=c; UpdateTokens(c); status.Text="授权完成。点击保存应用配置。"; } });
   Add(actions,"打开授权码网页",delegate { var c=Read(); c.Validate(true); var state=Guid.NewGuid().ToString("N"); Process.Start(new ProcessStartInfo(new BaiduApi(c,vault,null,CancellationToken.None,null).AuthorizationUrl(state)) { UseShellExecute=true }); status.Text="授权后复制百度显示的授权码，再点击“兑换授权码”。如果使用回调 URL，请核对 state="+state; });
   Add(actions,"兑换授权码",delegate { var code=MainWindow.Prompt("兑换授权码","粘贴百度授权后显示的一次性授权码。",""); if(code==null) return; Network(async delegate(CancellationToken ct) { var c=Read(); await Task.Run(()=>new BaiduApi(c,null,null,ct,null).ExchangeCode(code)); Result=c; UpdateTokens(c); status.Text="授权码兑换成功，点击保存应用配置。"; }); });
   Add(actions,"测试连接",delegate { Network(async delegate(CancellationToken ct) { var c=Read(); var result=await Task.Run(()=>new BaiduApi(c,null,null,ct,null).TestConnection()); Result=c; UpdateTokens(c); status.Text=result; }); });
   Add(actions,"创建应用内存档目录",delegate { Network(async delegate(CancellationToken ct) { var c=Read(); await Task.Run(()=>new BaiduApi(c,null,null,ct,null).EnsureRoot()); Result=c; UpdateTokens(c); status.Text="应用内存档目录已创建。"; }); });
   body.Children.Add(status); body.Children.Add(new TextBlock { Text="凭据使用 Windows 当前用户加密，只保存在这台电脑。配置页用于你自己的开发者应用；AppKey / Secret Key 必须来自已创建的百度网盘开放平台应用。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14) });
   var save=new Button { Content="保存并应用",Padding=new Thickness(14),IsDefault=true }; save.Click+=delegate { if(busy) return; try { var c=Read(); c.Validate(false); vault.Save(c); Result=c; DialogResult=true; } catch(Exception ex) { status.Text=ex.Message; } }; body.Children.Add(save); Closed+=delegate { if(operation!=null) operation.Cancel(); };
  }
  TextBox Field(string label,string value,StackPanel target=null) { target=target??body; target.Children.Add(new TextBlock { Text=label,Margin=new Thickness(0,0,0,6) }); var input=new TextBox { Text=value,Padding=new Thickness(8),Margin=new Thickness(0,0,0,12) }; target.Children.Add(input); return input; }
  PasswordBox Secret(string label,string value,StackPanel target=null) { target=target??body; target.Children.Add(new TextBlock { Text=label,Margin=new Thickness(0,0,0,6) }); var input=new PasswordBox { Password=value,Padding=new Thickness(8),Margin=new Thickness(0,0,0,12) }; target.Children.Add(input); return input; }
  void Add(Panel panel,string label,Action action) { var b=new Button { Content=label,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,8,8) }; b.Click+=delegate { if(busy) return; try { action(); } catch(Exception ex) { status.Text=ex.Message; } }; panel.Children.Add(b); }
  BaiduConfig Read() {
   int seconds,limit; if(!int.TryParse(timeout.Text,out seconds)||!int.TryParse(max.Text,out limit)) throw new InvalidOperationException("超时和大小上限必须为整数。");
   var c=new BaiduConfig { Enabled=enabled.IsChecked==true,AppKey=key.Text.Trim(),SecretKey=secret.Password.Trim(),CloudRoot=root.Text.Trim(),Scope=scope.Text.Trim(),RedirectUri=redirect.Text.Trim(),AccessToken=access.Password.Trim(),RefreshToken=refresh.Password.Trim(),TimeoutSeconds=seconds,MaxArchiveMB=limit,ExpiresAt=Result.ExpiresAt };
   c.ClearStaleCredentials(original,Result); return c;
  }
  void UpdateTokens(BaiduConfig c) { access.Password=c.AccessToken; refresh.Password=c.RefreshToken; original=c.Copy(); }
  async void Network(Func<CancellationToken,Task> action) { busy=true; operation=new CancellationTokenSource(); body.IsEnabled=false; status.Text="连接中…"; try { await action(operation.Token); } catch(OperationCanceledException) {} catch(Exception ex) { status.Text=ex.Message; } finally { busy=false; body.IsEnabled=true; operation.Dispose(); operation=null; } }
 }
}
