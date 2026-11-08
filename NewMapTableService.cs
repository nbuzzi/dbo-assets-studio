using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace DboAssetsStudio;

// Reusable additive map table adapter. Uses canonical RDF/text engines only.
internal static class NewMapTableService
{
    internal sealed record Identity(string Schema,uint WorldTableId,uint ResourceId,uint MapNameId,string Folder,string Label,uint TemplateWorldId,uint TemplateZoneId,uint ReturnWorldId,string ReturnLocation);
    internal sealed record Pin(string Path,string Sha256,long Bytes);
    internal sealed record Plan(string Profile,Pin Identity,Pin World,Pin Zone,Pin Text,Pin Scene);
    private static readonly JsonSerializerOptions Strict=new(ContentSafety.Json){PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private static Pin Seal(string path){path=Path.GetFullPath(path);ContentSafety.NoLinks(path);long bytes=new FileInfo(path).Length;if(bytes is <1 or >128*1024*1024)throw new IOException("Bounded map table input required.");return new(path,ContentSafety.Hash(path),bytes);}
    private static T Read<T>(string path){byte[]bytes=File.ReadAllBytes(path);using var d=JsonDocument.Parse(bytes);TerrainGeometryService.NoDuplicateKeys(d.RootElement);return JsonSerializer.Deserialize<T>(bytes,Strict)??throw new InvalidDataException("Null map table input.");}
    internal static Identity ReadIdentity(string path){var i=Read<Identity>(path);if(i.Schema!="assets-new-map-identity-v1"||new[]{i.WorldTableId,i.ResourceId,i.MapNameId,i.TemplateWorldId,i.TemplateZoneId,i.ReturnWorldId}.Any(x=>x==0||x==uint.MaxValue)||i.WorldTableId==i.TemplateWorldId||i.MapNameId==i.TemplateZoneId||i.Folder.Length>32||!Regex.IsMatch(i.Folder,@"^dbo_assets_[a-z0-9_]{1,20}$")||string.IsNullOrWhiteSpace(i.Label)||i.Label.Length>40)throw new InvalidDataException("Invalid new map identity.");return i;}
    private static Pin[] Inputs(Plan p)=>[p.Identity,p.World,p.Zone,p.Text,p.Scene];
    private static void ValidateReservations(Plan p,Identity identity)
    {
        var(world,key)=RdfCli.Open(p.World.Path);
        if(world.Schema.Name!="WorldTable")throw new InvalidDataException("Native World schema required.");
        string Value(RdfDocument doc,RdfRow row,string name)=>doc.Schema.Decode(doc.Schema.Fields.Single(f=>f.Name==name),row.Current);
        foreach(var row in world.Rows){
            if(world.Schema.Decode(key,row.Current)==identity.WorldTableId.ToString()||Value(world,row,"dwWorldResourceID")==identity.ResourceId.ToString()||
                Value(world,row,"szName").Equals(identity.Folder,StringComparison.OrdinalIgnoreCase)||Value(world,row,"wszResourceFolder").TrimEnd('\\','/').Equals(identity.Folder,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("New map World/resource/folder already reserved in source rows.");
        }
        var(zone,zoneKey)=RdfCli.Open(p.Zone.Path);
        if(zone.Schema.Name!="WorldZoneTable")throw new InvalidDataException("Native WorldZone schema required.");
        foreach(var row in zone.Rows)if(zone.Schema.Decode(zoneKey,row.Current)==identity.MapNameId.ToString()||Value(zone,row,"nameTblidx")==identity.MapNameId.ToString())
            throw new InvalidDataException("New zone/map label already reserved, including existing references.");
    }
    internal static object CreatePlan(string identity,string world,string zone,string text,string scene,string output){ReadIdentity(identity);var p=new Plan("assets-new-map-tables-plan-v1",Seal(identity),Seal(world),Seal(zone),Seal(text),Seal(scene));output=ContentSafety.SafeOutput(output);if(Inputs(p).Any(x=>x.Path.Equals(output,StringComparison.OrdinalIgnoreCase)))throw new IOException("Map table plan overlaps input.");ContentSafety.WriteJson(output,p);return new{plan=output,readyToInstall=false};}
    internal static object Run(string path,string?output){path=Path.GetFullPath(path);ContentSafety.NoLinks(path);var locks=new List<FileStream>();string?pending=null;
        try{locks.Add(new(path,FileMode.Open,FileAccess.Read,FileShare.Read));if(new FileInfo(path).Length>16384)throw new IOException("Bounded map table plan required.");var p=Read<Plan>(path);if(p.Profile!="assets-new-map-tables-plan-v1")throw new InvalidDataException("Wrong map table plan profile.");foreach(var pin in Inputs(p)){ContentSafety.NoLinks(pin.Path);locks.Add(new(pin.Path,FileMode.Open,FileAccess.Read,FileShare.Read));if(Seal(pin.Path)!=pin)throw new IOException("Stale map table source: "+pin.Path);}
            var identity=ReadIdentity(p.Identity.Path);ValidateReservations(p,identity);using var scene=JsonDocument.Parse(File.ReadAllBytes(p.Scene.Path));var s=scene.RootElement;if(s.GetProperty("profile").GetString()!="assets-map-preview-scene-v1")throw new InvalidDataException("Canonical authored scene required.");var r=s.GetProperty("recipe");float radius=r.GetProperty("radius").GetSingle(),height=r.GetProperty("deckHeight").GetSingle();var spec=s.GetProperty("markers")[2];
            if(output==null)return new{pinsValid=true,identity,readyToInstall=false};output=ContentSafety.SafeOutput(output);string prefix=output.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(Inputs(p).Select(x=>x.Path).Append(path).Any(x=>x.Equals(output,StringComparison.OrdinalIgnoreCase)||x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new IOException("Map table output overlaps input.");pending=ContentSafety.FreshDirectory(output);
            static string V(float x,float y,float z)=>FormattableString.Invariant($"{x:R} {y:R} {z:R}");string spectator=V(spec.GetProperty("x").GetSingle(),spec.GetProperty("y").GetSingle(),spec.GetProperty("z").GetSingle());
            var fields=new Dictionary<string,object>{{"szName",identity.Folder},{"wszName",identity.Folder},{"bDynamic",1},{"nCreateCount",8},{"dwDynamicCreateCountShareGroup",uint.MaxValue},{"dwDestroyTimeInMilliSec",120000},{"wszMobSpawn_Table_Name",""},{"wszNpcSpawn_Table_Name",""},{"wszObjSpawn_Table_Name",""},{"vStart","1024 0 1024"},{"vEnd","-1024 0 -1024"},{"vStandardLoc","0 0 0"},{"vDefaultLoc",V(-radius*.55f,height,0)},{"vDefaultDir","1 0 0"},{"vStart1Loc",V(-radius*.55f,height,0)},{"vStart1Dir","1 0 0"},{"vStart2Loc",V(radius*.55f,height,0)},{"vStart2Dir","-1 0 0"},{"vSpectatorStartLoc",spectator},{"vSpectatorEndLoc",spectator},{"vWaitingPoint1Loc",spectator},{"vWaitingPoint2Loc",spectator},{"vWaitingPoint1Dir","0 0 1"},{"vWaitingPoint2Dir","0 0 1"},{"vBattleStartLoc",V(radius,0,radius)},{"vBattleEndLoc",V(-radius,0,-radius)},{"vBattleStart2Loc",V(radius,0,radius)},{"vBattleEnd2Loc",V(-radius,0,-radius)},{"vOutSideBattleStartLoc",V(radius+20,0,radius+20)},{"vOutSideBattleEndLoc",V(-radius-20,0,-radius-20)},{"fSplitSize",128},{"wFuncFlag",0},{"byWorldRuleType",0},{"worldRuleTbldx",uint.MaxValue},{"outWorldTblidx",identity.ReturnWorldId},{"outWorldLoc",identity.ReturnLocation},{"outWorldDir","1 0 0"},{"wszResourceFolder",identity.Folder+"\\"},{"dwWorldResourceID",identity.ResourceId},{"wpsLinkIndex",uint.MaxValue},{"dwProhibition_Bit_Flag",1}};
            string wp=Path.Combine(pending,"world-patch.json"),zp=Path.Combine(pending,"zone-patch.json"),tp=Path.Combine(pending,"text-patch.json");
            ContentSafety.WriteJson(wp,new{sourceSha256=p.World.Sha256,operations=new[]{new{op="clone",templateId=identity.TemplateWorldId.ToString(),newId=identity.WorldTableId.ToString(),fields}}});
            ContentSafety.WriteJson(zp,new{sourceSha256=p.Zone.Sha256,operations=new[]{new{op="clone",templateId=identity.TemplateZoneId.ToString(),newId=identity.MapNameId.ToString(),fields=new Dictionary<string,object>{{"wFunctionBitFlag",0},{"worldTblidx",identity.WorldTableId},{"nameTblidx",identity.MapNameId},{"wszName_Text",identity.Label},{"bForbidden_Vehicle",1}}}}});
            ContentSafety.WriteJson(tp,new{targetSha256=p.Text.Sha256,reason="Add original map label without changing existing text",category="map",records=new[]{new{id=identity.MapNameId.ToString(),text=identity.Label}}});
            RdfCli.Run(["apply",p.World.Path,wp,Path.Combine(pending,"world")]);RdfCli.Run(["apply",p.Zone.Path,zp,Path.Combine(pending,"zone")]);RdfCli.Run(["text-add",p.Text.Path,tp,Path.Combine(pending,"text")]);
            if(Inputs(p).Any(x=>Seal(x.Path)!=x))throw new IOException("Map table inputs changed.");File.Copy(path,Path.Combine(pending,"plan.json"));ContentSafety.WriteJson(Path.Combine(pending,"identity.json"),identity);
            var resources=Directory.GetFiles(pending,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(x=>new{path=Path.GetRelativePath(pending,x),sha256=ContentSafety.Hash(x),size=new FileInfo(x).Length}).ToArray();
            var audit=new{profile="assets-new-map-tables-candidate-v1",utc=DateTimeOffset.UtcNow,inputs=Inputs(p),identity,resources,nativeRdfGameplayTested=false,readyToInstall=false};ContentSafety.WriteJson(Path.Combine(pending,"audit.json"),audit);Directory.Move(pending,output);pending=null;return new{output,audit};
        }finally{foreach(var l in locks)l.Dispose();if(pending!=null&&Directory.Exists(pending))Directory.Delete(pending,true);}}
}
