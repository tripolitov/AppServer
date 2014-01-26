using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using NuGet;
using NuGet.Resources;

namespace Inceptum.AppServer.AppDiscovery.NuGet
{

    static class NugetExtensions
    {
        internal static IEnumerable<T> GetCompatibleItemsCore<T>(this IProjectSystem projectSystem, IEnumerable<T> items) where T : IFrameworkTargetable
        {
            IEnumerable<T> compatibleItems;
            if (VersionUtility.TryGetCompatibleItems<T>(projectSystem.TargetFramework, items, out compatibleItems))
                return compatibleItems;
                
            return Enumerable.Empty<T>();
        }


        internal static bool IsPortableFramework(this FrameworkName framework)
        {
            if (framework != null)
                return ".NETPortable".Equals(framework.Identifier, StringComparison.OrdinalIgnoreCase);
                
            return false;
        }
 

    }


    class ApplicationProjectManager : ProjectManager
    {
        private readonly IPackageRepository m_SharedRepository;
        private string m_PackageId;

        public ApplicationProjectManager(string packageId, IPackageRepository sourceRepository, IPackagePathResolver pathResolver, IProjectSystem project, IPackageRepository localRepository, IPackageRepository sharedRepository)
            : base(sourceRepository, pathResolver, project, localRepository)
        {
            m_PackageId = packageId;
            m_SharedRepository = sharedRepository;
        }

        private void execute(IPackage package, IPackageOperationResolver resolver)
        {
            IEnumerable<PackageOperation> source = resolver.ResolveOperations(package);
            if (source.Any())
            {
                foreach (PackageOperation operation in source)
                    Execute(operation);
            }
            else
            {
                if (!LocalRepository.Exists(package))
                    return;
                Logger.Log(MessageLevel.Info, NuGetResources.Log_ProjectAlreadyReferencesPackage, (object)this.Project.ProjectName, (object)PackageExtensions.GetFullName((IPackageMetadata)package));
            }
        }

        public override void AddPackageReference(IPackage package, bool ignoreDependencies, bool allowPrereleaseVersions)
        {
         //   InstallWalker walker = new InstallWalker(this.LocalRepository, this.SourceRepository, Project.TargetFramework, this.Logger, ignoreDependencies, allowPrereleaseVersions);
            //base.AddPackageReference(package, ignoreDependencies, allowPrereleaseVersions);

            var walker = new UpdateWalker(LocalRepository, SourceRepository, new DependentsWalker(LocalRepository, Project.TargetFramework), ConstraintProvider,
                Project.TargetFramework, NullLogger.Instance, !ignoreDependencies, allowPrereleaseVersions)
            {
                AcceptedTargets = PackageTargets.All
            };
            execute(package, walker);
            
        }


        private void filterAssemblyReferences(List<IPackageAssemblyReference> assemblyReferences, ICollection<PackageReferenceSet> packageAssemblyReferences)
        {
            if (packageAssemblyReferences != null && packageAssemblyReferences.Count > 0)
            {
                var packageReferences = Project.GetCompatibleItemsCore(packageAssemblyReferences).FirstOrDefault();
                if (packageReferences != null)
                {
                    // remove all assemblies of which names do not appear in the References list
                    assemblyReferences.RemoveAll(assembly => !packageReferences.References.Contains(assembly.Name, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
        protected override void ExtractPackageFilesToProject(IPackage package)
        {
            // Resolve assembly references and content files first so that if this fails we never do anything to the project
            List<IPackageAssemblyReference> assemblyReferences = Project.GetCompatibleItemsCore(package.AssemblyReferences).ToList();
            List<IPackageFile> contentFiles = Project.GetCompatibleItemsCore(package.GetContentFiles()).ToList();
            IEnumerable<IPackageFile> configItems;
            Project.TryGetCompatibleItems(package.GetFiles("config"), out configItems);
            var configFiles = configItems.ToArray();


            // If the package doesn't have any compatible assembly references or content files,
            // throw, unless it's a meta package.
            if (assemblyReferences.Count == 0  && contentFiles.Count == 0  &&( package.AssemblyReferences.Any() || package.GetContentFiles().Any()  ))
            {
                // for portable framework, we want to show the friendly short form (e.g. portable-win8+net45+wp8) instead of ".NETPortable, Profile=Profile104".
                FrameworkName targetFramework = Project.TargetFramework;
                string targetFrameworkString = targetFramework.IsPortableFramework()
                                                    ? VersionUtility.GetShortFrameworkName(targetFramework)
                                                    : targetFramework != null ? targetFramework.ToString() : null;

                throw new InvalidOperationException(
                           String.Format(CultureInfo.CurrentCulture,
                           NuGetResources.UnableToFindCompatibleItems, package.GetFullName(), targetFrameworkString));
            }

            // IMPORTANT: this filtering has to be done AFTER the 'if' statement above,
            // so that we don't throw the exception in case the <References> filters out all assemblies.
            filterAssemblyReferences(assemblyReferences, package.PackageAssemblyReferences);

            try
            {
                // Add content files
                if (m_PackageId==package.Id)
                    Project.AddFiles(contentFiles);

                // Add config files
                var configTransformers = configFiles.Select(f => Path.GetExtension(f.Path)).Distinct().ToDictionary(e => new FileTransformExtensions(e, e), e => (IPackageFileTransformer)new ConfigTransformer(e));
                Project.AddFiles(configFiles, configTransformers);
    

                // Add the references to the reference path
                foreach (IPackageAssemblyReference assemblyReference in assemblyReferences)
                {
                    if (assemblyReference.IsEmptyFolder())
                    {
                        continue;
                    }

                    // Get the physical path of the assembly reference
                    string referencePath = Path.Combine(PathResolver.GetInstallPath(package), assemblyReference.Path);
                    string relativeReferencePath = PathUtility.GetRelativePath(Project.Root, referencePath);

                    if (Project.ReferenceExists(assemblyReference.Name))
                    {
                        Project.RemoveReference(assemblyReference.Name);
                    }

                    // The current implementation of all ProjectSystem does not use the Stream parameter at all.
                    // We can't change the API now, so just pass in a null stream.
                    Project.AddReference(relativeReferencePath, assemblyReference.GetStream());
                }


            }
            finally
            {
                m_SharedRepository.AddPackage(package);
                LocalRepository.AddPackage(package);
            }
        }

/*
        protected override void ExtractPackageFilesToProject(IPackage package)
        {
            base.ExtractPackageFilesToProject(package); 
            IEnumerable<IPackageFile> configItems;
            Project.TryGetCompatibleItems(package.GetFiles("config"), out configItems);
            var configFiles = configItems.ToArray();
            if (!configFiles.Any() && package.GetFiles("config").Any())
            {
                // for portable framework, we want to show the friendly short form (e.g. portable-win8+net45+wp8) instead of ".NETPortable, Profile=Profile104".
                FrameworkName targetFramework = Project.TargetFramework;
                string targetFrameworkString = /*targetFramework.IsPortableFramework()#1#targetFramework != null && ".NETPortable".Equals(targetFramework.Identifier, StringComparison.OrdinalIgnoreCase)
                    ? VersionUtility.GetShortFrameworkName(targetFramework)
                    : targetFramework != null ? targetFramework.ToString() : null;

                throw new InvalidOperationException(
                    String.Format(CultureInfo.CurrentCulture,
                        NuGetResources.UnableToFindCompatibleItems, package.GetFullName(), targetFrameworkString));
            }

            if (configFiles.Any())
            {
                Logger.Log(MessageLevel.Debug, ">> {0} are being added from '{1}'{2}", "config files",
                    Path.GetDirectoryName(configFiles[0].Path), GetTargetFrameworkLogString(configFiles[0].TargetFramework));
            }
            // Add config files
            Project.AddFiles(configFiles, new Dictionary<FileTransformExtensions, IPackageFileTransformer>());
        }
*/
        public static string GetTargetFrameworkLogString(FrameworkName targetFramework)
        {
            return (targetFramework == null || targetFramework == VersionUtility.EmptyFramework) ? "(not framework-specific)" : String.Empty;
        }



    }

     internal class ConfigTransformer : IPackageFileTransformer
    {
         private string m_Extension;

         public ConfigTransformer(string extension)
         {
             m_Extension = extension;
         }

         public void TransformFile(IPackageFile file, string targetPath, IProjectSystem projectSystem)
         {
             var path = targetPath + m_Extension;
             if (path.ToLower().StartsWith("config\\"))
                 path = path.Substring(7);
             if (projectSystem.FileExists(path))
                projectSystem.AddFile(path + ".default", file.GetStream());
            else
                projectSystem.AddFile(path, file.GetStream());
         }

         public void RevertFile(IPackageFile file, string targetPath, IEnumerable<IPackageFile> matchingFiles, IProjectSystem projectSystem)
        {
         
        }
    } 
}