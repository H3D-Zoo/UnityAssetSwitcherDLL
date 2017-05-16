using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.CommandLine;
using System.Reflection;
using System.IO;

namespace AssetSwitcherDLL
{
    class Program
    {
        static void Main(string[] args)
        {
            IReadOnlyList<string> assemblyfiles = Array.Empty<string>();
            var metadir = ".";
            string assetdir = null;
            bool revert = false;
            bool ab = false;
            string pattern = ".*";
            ArgumentSyntax.Parse(args, synatx =>
             {
                 synatx.DefineOption("m|metadir", ref metadir, "The .meta files to resolve guid");
                 synatx.DefineOption("a|assetdir", ref assetdir, "The Asset Fold to resolve asset's type reference");
                 synatx.DefineOption("p|pattern", ref pattern, "The Asset pattern to specify asset file default:.*");
                 synatx.DefineOption("r|revert", ref revert, "Revert Asset Modfiy");
                 synatx.DefineOption("ab|assetbundle", ref ab, "AssetBundle 模式");
                 synatx.DefineParameterList("assembly", ref assemblyfiles, "The assembly files to resolve type");
             });
            var assemblies = assemblyfiles.Select(file =>
                {
                try
                {
                        Console.WriteLine(string.Format("Load Assembly: {0}",new FileInfo(file).FullName));
                        return Assembly.LoadFile(new FileInfo(file).FullName);
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("Load Assembly Failed Exception: {0}", e));
                    return null;
                }
                }
              ).Where(assembly=> assembly!=null)
              //LINQ 是延迟求值
              .ToList();
            try
            {
                var asset_modify = new AssetModify(
                    assemblies,
                    new DirectoryInfo(metadir),
                    string.IsNullOrEmpty(assetdir) ? null : new DirectoryInfo(assetdir)
                    );

                if (!revert)
                {
                    asset_modify.BuildAssemblyInfo(pattern);
                    asset_modify.ModifyAsset();
                }
                else
                {
                    asset_modify.RevertAsset();
                }
            }
            catch(Exception e)
            {
                Console.WriteLine(string.Format("AssetModify Failed Exception: {0}", e));
            }
        }
    }
}
