using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using DicomRtNifti.Web.Hosting;
using DicomRtNifti.Web.Tests.Support;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// One Core, three front-ends (plan section 3.4), enforced by tests rather than review:
    /// the web assembly references no fo-dicom or SimpleITK type, calls none of the Core
    /// functions that would put naming, hashing, manifest or rasterization logic in the web
    /// layer, and touches the filesystem only inside <see cref="DataRootResolver"/> and the server
    /// state types listed in <see cref="FileSystemAllowed"/>.
    ///
    /// The checks read the compiled assembly's IL rather than its source, so they see Razor
    /// components, lambdas and async methods as compiled, and are not fooled by an alias, a
    /// <c>using static</c> or a comment.
    /// </summary>
    public sealed class ArchitectureTests
    {
        private static readonly Assembly Web = typeof(Program).Assembly;

        /// <summary>
        /// The only types whose code may call a filesystem API: the resolver, and the four types
        /// that keep server state (the job store, the per-project job locks, the audit log, the
        /// scan snapshots). Every path those four touch comes from <see cref="DataRootResolver.ResolveServerPath"/>, so
        /// the resolver's checks still stand between them and the disk.
        /// </summary>
        private static readonly Type[] FileSystemAllowed =
        {
            typeof(DataRootResolver),
            typeof(DicomRtNifti.Web.Jobs.JobStore),
            typeof(DicomRtNifti.Web.Jobs.ProjectLocks),
            typeof(DicomRtNifti.Web.Audit.AuditLog),
            typeof(DicomRtNifti.Web.State.ScanSnapshotStore),
        };

        private static readonly HashSet<string> FileSystemTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IO.File",
            "System.IO.Directory",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo",
            "System.IO.FileSystemInfo",
            "System.IO.DriveInfo",
            "System.IO.FileStream",
            "System.IO.FileSystemWatcher",
            "System.IO.RandomAccess",
            "System.IO.Enumeration.FileSystemEnumerable`1",
            "System.IO.Enumeration.FileSystemEnumerator`1",
            "System.IO.MemoryMappedFiles.MemoryMappedFile",
            "Microsoft.Extensions.FileProviders.PhysicalFileProvider",
        };

        private static readonly Lazy<IReadOnlyList<Call>> Calls = new Lazy<IReadOnlyList<Call>>(ScanCalls);

        [Fact]
        public void The_web_assembly_references_neither_fo_dicom_nor_simpleitk()
        {
            var names = Web.GetReferencedAssemblies().Select(a => a.Name).ToList();

            Assert.DoesNotContain(names, n => n.StartsWith("fo-dicom", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.StartsWith("FellowOakDicom", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.StartsWith("SimpleITK", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("DicomRtNifti.Core", names);
        }

        [Fact]
        public void The_web_project_has_no_fo_dicom_package_reference()
        {
            var project = XDocument.Load(Path.Combine(RepoPaths.WebProject, "DicomRtNifti.Web.csproj"));

            var packages = project.Descendants("PackageReference").Select(p => (string)p.Attribute("Include")).ToList();

            Assert.DoesNotContain(packages, p => p.Contains("dicom", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void No_web_code_names_a_fo_dicom_or_simpleitk_member()
        {
            var offending = Calls.Value
                .Where(c => c.Namespace.StartsWith("FellowOakDicom", StringComparison.Ordinal)
                    || c.Namespace.StartsWith("itk.simple", StringComparison.Ordinal))
                .ToList();

            Assert.True(offending.Count == 0, Describe(offending));
        }

        [Fact]
        public void No_web_code_calls_core_naming_hashing_manifest_or_rasterization()
        {
            var offending = Calls.Value.Where(IsForbiddenCoreCall).ToList();

            Assert.True(offending.Count == 0, Describe(offending));
        }

        [Fact]
        public void Only_the_resolver_touches_the_filesystem()
        {
            var fileSystem = Calls.Value.Where(IsFileSystemCall).ToList();
            var offending = fileSystem.Where(c => !FileSystemAllowed.Contains(c.Owner)).ToList();

            Assert.True(offending.Count == 0, Describe(offending));
            // The scan is not vacuous: it does see the resolver's own calls.
            Assert.Contains(fileSystem, c => c.Owner == typeof(DataRootResolver) && c.DeclaringType == "System.IO.Directory");
        }

        [Fact]
        public void The_scan_sees_calls_into_core()
        {
            Assert.Contains(Calls.Value, c => c.DeclaringType == "DicomRtNifti.Core.Services.SimpleItkNative" && c.Member == "TryProbe");
        }

        [Fact]
        public void Every_token_in_the_web_assembly_resolves()
        {
            var unresolved = Calls.Value.Where(c => c.DeclaringType == null).ToList();

            Assert.True(unresolved.Count == 0, Describe(unresolved));
        }

        private static bool IsForbiddenCoreCall(Call c)
        {
            if (c.DeclaringType == null || !c.DeclaringType.StartsWith("DicomRtNifti.Core.", StringComparison.Ordinal))
                return false;
            string type = c.DeclaringType.Substring(c.DeclaringType.LastIndexOf('.') + 1);
            return type == "WindowsPathSanitizer"
                || type == "HashNaming"
                || (c.Member.StartsWith("Get", StringComparison.Ordinal) && c.Member.EndsWith("Hash", StringComparison.Ordinal))
                || (type == "ExportManifestService" && c.Member.StartsWith("Write", StringComparison.Ordinal))
                || (type == "NiftiConversionService" && c.Member.StartsWith("Convert", StringComparison.Ordinal))
                || c.Member == "RasterizeRois";
        }

        private static bool IsFileSystemCall(Call c)
        {
            if (c.DeclaringType == null)
                return false;
            if (FileSystemTypes.Contains(c.DeclaringType))
                return true;
            if (c.DeclaringType == "System.IO.Path")
                return c.Member is "GetFullPath" or "GetTempPath" or "GetTempFileName";
            // StreamReader(string path) and StreamWriter(string path) open files; over a Stream they do not.
            return (c.DeclaringType == "System.IO.StreamReader" || c.DeclaringType == "System.IO.StreamWriter")
                && c.Member == ".ctor" && c.FirstParameter == typeof(string);
        }

        private static string Describe(IEnumerable<Call> calls) =>
            string.Join(Environment.NewLine, calls.Select(c => $"{c.Owner?.FullName} -> {c.DeclaringType ?? "<unresolved>"}.{c.Member}"));

        private sealed class Call
        {
            /// <summary>The outermost type whose code makes the call (lambdas and state machines fold into it).</summary>
            public Type Owner { get; set; }
            public string DeclaringType { get; set; }
            public string Namespace { get; set; } = "";
            public string Member { get; set; }
            public Type FirstParameter { get; set; }
        }

        private static IReadOnlyList<Call> ScanCalls()
        {
            var opCodes = OpCodeTable();
            var calls = new List<Call>();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var type in Web.GetTypes())
            {
                var owner = type;
                while (owner.DeclaringType != null)
                    owner = owner.DeclaringType;
                var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;

                var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
                foreach (var method in methods)
                {
                    byte[] il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il == null)
                        continue;
                    var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                    foreach (int token in MemberTokens(il, opCodes))
                        calls.Add(Resolve(type.Module, token, typeArgs, methodArgs, owner));
                }
            }

            return calls;
        }

        private static Call Resolve(Module module, int token, Type[] typeArgs, Type[] methodArgs, Type owner)
        {
            try
            {
                var member = module.ResolveMember(token, typeArgs, methodArgs);
                var declaring = member is Type t ? t : member.DeclaringType;
                return new Call
                {
                    Owner = owner,
                    DeclaringType = TypeName(declaring),
                    Namespace = declaring?.Namespace ?? "",
                    Member = member is Type ? "<type>" : member.Name,
                    FirstParameter = (member as MethodBase)?.GetParameters().FirstOrDefault()?.ParameterType,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException or BadImageFormatException or FileNotFoundException)
            {
                return new Call { Owner = owner, Member = $"token 0x{token:X8} ({ex.GetType().Name})" };
            }
        }

        private static string TypeName(Type type)
        {
            if (type == null)
                return "<module>";
            if (type.IsGenericParameter)
                return "<generic parameter " + type.Name + ">";
            if (type.HasElementType)
                return TypeName(type.GetElementType()) + (type.IsArray ? "[]" : type.IsPointer ? "*" : "&");
            return (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name;
        }

        /// <summary>The metadata tokens of every method, field and type operand in a method body.</summary>
        private static IEnumerable<int> MemberTokens(byte[] il, (OpCode[] One, OpCode[] Two) opCodes)
        {
            int i = 0;
            while (i < il.Length)
            {
                OpCode op = il[i] == 0xFE ? opCodes.Two[il[++i]] : opCodes.One[il[i]];
                i++;
                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        yield return BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        i += 1;
                        break;
                    case OperandType.InlineVar:
                        i += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8;
                        break;
                    case OperandType.InlineSwitch:
                        i += 4 + 4 * BitConverter.ToInt32(il, i);
                        break;
                    default:
                        // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR.
                        i += 4;
                        break;
                }
            }
        }

        private static (OpCode[] One, OpCode[] Two) OpCodeTable()
        {
            var one = new OpCode[0x100];
            var two = new OpCode[0x100];
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)field.GetValue(null);
                ushort value = unchecked((ushort)op.Value);
                if (op.Size == 1)
                    one[value] = op;
                else
                    two[value & 0xFF] = op;
            }

            return (one, two);
        }
    }
}
