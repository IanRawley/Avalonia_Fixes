using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Platform.Internal;
using Avalonia.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using XamlX.TypeSystem;

namespace Avalonia.Build.Tasks
{
    public static partial class XamlCompilerTaskExecutor
    {
        interface IResource : IFileSource
        {
            string Uri { get; }
            string Name { get; }
            void Remove();

        }

        interface IResourceGroup
        {
            string Name { get; }
            IEnumerable<IResource> Resources { get; }
        }

        class AvaloniaResources : IResourceGroup
        {
            private readonly AssemblyDefinition _asm;
            Dictionary<string, AvaloniaResource> _resources = new Dictionary<string, AvaloniaResource>();
            private EmbeddedResource _embedded;
            public AvaloniaResources(AssemblyDefinition asm, string projectDir)
            {
                _asm = asm;
                _embedded = ((EmbeddedResource)asm.MainModule.Resources.FirstOrDefault(r =>
                    r.ResourceType == ResourceType.Embedded && r.Name == Constants.AvaloniaResourceName));
                if (_embedded == null)
                    return;
                using (var stream = _embedded.GetResourceStream())
                {
                    var br = new BinaryReader(stream);
                    var index = AvaloniaResourcesIndexReaderWriter.ReadIndex(new MemoryStream(br.ReadBytes(br.ReadInt32())));
                    var baseOffset = stream.Position;
                    foreach (var e in index)
                    {
                        stream.Position = e.Offset + baseOffset;
                        _resources[e.Path] = new AvaloniaResource(this, projectDir, e.Path, br.ReadBytes(e.Size));
                    }
                }
            }

            public void Save()
            {
                if (_embedded != null)
                {
                    _asm.MainModule.Resources.Remove(_embedded);
                    _embedded = null;
                }

                if (_resources.Count == 0)
                    return;

                var output = new MemoryStream();

                AvaloniaResourcesIndexReaderWriter.WriteResources(
                    output,
                    _resources.Select(x => new AvaloniaResourcesEntry
                    {
                        Path = x.Key,
                        Size = x.Value.FileContents.Length,
                        SystemPath = x.Value.FilePath,
                        Open = () => new MemoryStream(x.Value.FileContents)
                    }).ToList());

                output.Position = 0L;
                _embedded = new EmbeddedResource(Constants.AvaloniaResourceName, ManifestResourceAttributes.Public, output);
                _asm.MainModule.Resources.Add(_embedded);
            }

            public string Name => "AvaloniaResources";
            public IEnumerable<IResource> Resources => _resources.Values.ToList();

            class AvaloniaResource : IResource
            {
                private readonly AvaloniaResources _grp;
                private readonly byte[] _data;

                public AvaloniaResource(AvaloniaResources grp,
                    string projectDir,
                    string name, byte[] data)
                {
                    _grp = grp;
                    _data = data;
                    Name = name;
                    FilePath = Path.Combine(projectDir, name.TrimStart('/'));
                    Uri = $"avares://{grp._asm.Name.Name}/{name.TrimStart('/')}";
                }
                public string Uri { get; }
                public string Name { get; }
                public string FilePath { get; }
                public byte[] FileContents => _data;

                public void Remove() => _grp._resources.Remove(Name);
            }
        }

        static void CopyDebugDocument(MethodDefinition method, MethodDefinition copyFrom)
        {
            if (!copyFrom.DebugInformation.HasSequencePoints)
                return;
            var dbg = method.DebugInformation;

            dbg.Scope = new ScopeDebugInformation(method.Body.Instructions.First(), method.Body.Instructions.First())
            {
                End = new InstructionOffset(),
                Import = new ImportDebugInformation()
            };
            dbg.SequencePoints.Add(new SequencePoint(method.Body.Instructions.First(),
                copyFrom.DebugInformation.SequencePoints.First().Document)
            {
                StartLine = 0xfeefee,
                EndLine = 0xfeefee
            });

        }
        

        private static bool MatchThisCall(Collection<Instruction> instructions, int idx)
        {
            var i = instructions[idx];
            // A "normal" way of passing `this` to a static method:
            
            // ldarg.0
            // call void [Avalonia.Markup.Xaml]Avalonia.Markup.Xaml.AvaloniaXamlLoader::Load(object)
            
            if (i.OpCode == OpCodes.Ldarg_0 || (i.OpCode == OpCodes.Ldarg && i.Operand?.Equals(0) == true))
                return true;

            /* F# way of using `this` in constructor emits a monstrosity like this:
                IL_01c7: ldarg.0
                IL_01c8: ldfld class [FSharp.Core]Microsoft.FSharp.Core.FSharpRef`1<class FVim.Cursor> FVim.Cursor::this
                IL_01cd: call instance !0 class [FSharp.Core]Microsoft.FSharp.Core.FSharpRef`1<class FVim.Cursor>::get_contents()
                IL_01d2: call !!0 [FSharp.Core]Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions::CheckThis<class FVim.Cursor>(!!0)
                IL_01d7: call void [Avalonia.Markup.Xaml]Avalonia.Markup.Xaml.AvaloniaXamlLoader::Load(object)
                
                We check for the previous call to be Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions::CheckThis
                since it actually returns `this`
            */
            if (i.OpCode == OpCodes.Call
                && i.Operand is GenericInstanceMethod gim
                && gim.Name == "CheckThis"
                && gim.DeclaringType.FullName == "Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions")
                return true;

            return false;
        }
    }
 
}
