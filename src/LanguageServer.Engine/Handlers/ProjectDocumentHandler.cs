using Lsp;
using Lsp.Capabilities.Server;
using Lsp.Models;
using Lsp.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Language.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NuGet.Configuration;
using NuGet.Versioning;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace MSBuildProjectTools.LanguageServer.Handlers
{
    using System.Text;
    using Documents;
    using MSBuild;
    using Utilities;

    /// <summary>
    ///     Handler for project file document events.
    /// </summary>
    public class ProjectDocumentHandler
        : TextDocumentHandler
    {
        /// <summary>
        ///     Documents for loaded project, keyed by document URI.
        /// </summary>
        readonly ConcurrentDictionary<Uri, ProjectDocument> _projectDocuments = new ConcurrentDictionary<Uri, ProjectDocument>();

        /// <summary>
        ///     Create a new <see cref="ProjectDocumentHandler"/>.
        /// </summary>
        /// <param name="server">
        ///     The language server.
        /// </param>
        /// <param name="logger">
        ///     The application logger.
        /// </param>
        /// <param name="projectDocuments"/>
        ///     Documents for loaded project files.
        /// </param>
        public ProjectDocumentHandler(ILanguageServer server, ILogger logger)
            : base(server, logger)
        {
            Options.Change = TextDocumentSyncKind.Full;
        }

        /// <summary>
        ///     The document selector that describes documents targeted by the handler.
        /// </summary>
        protected override DocumentSelector DocumentSelector => new DocumentSelector(
            new DocumentFilter
            {
                Pattern = "**/*.*proj",
                Language = "xml"
            },
            new DocumentFilter
            {
                Pattern = "**/*.props",
                Language = "xml"
            },
            new DocumentFilter
            {
                Pattern = "**/*.targets",
                Language = "xml"
            }
        );

        /// <summary>
        ///     Called when a text document is opened.
        /// </summary>
        /// <param name="parameters">
        ///     The notification parameters.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task OnDidOpenTextDocument(DidOpenTextDocumentParams parameters)
        {
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri);
            PublishDiagnostics(projectDocument);
            
            if (!projectDocument.HasXml)
            {
                Log.Warning("Failed to load project file {ProjectFilePath}.", projectDocument.ProjectFile.FullName);

                return;
            }

            Log.Information("Successfully loaded project {ProjectFilePath}.", projectDocument.ProjectFile.FullName);
            Log.Verbose("===========================");
            
            foreach (PackageSource packageSource in projectDocument.ConfiguredPackageSources)
            {
                Log.Verbose(" - Project uses package source {PackageSourceName} ({PackageSourceUrl})",
                    packageSource.Name,
                    packageSource.Source
                );
            }

            foreach (MSBuildObject msbuildObject in projectDocument.MSBuildObjects)
            {
                Log.Information("MSBuildObject: {Kind} {Name} spanning {XmlRange}",
                    msbuildObject.Kind,
                    msbuildObject.Name,
                    msbuildObject.XmlRange
                );
            }
        }

        /// <summary>
        ///     Called when a text document is opened.
        /// </summary>
        /// <param name="parameters">
        ///     The notification parameters.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task OnDidChangeTextDocument(DidChangeTextDocumentParams parameters)
        {
            TextDocumentContentChangeEvent mostRecentChange = parameters.ContentChanges.LastOrDefault();
            if (mostRecentChange == null)
                return;

            string updatedDocumentText = mostRecentChange.Text;
            ProjectDocument projectDocument = await TryUpdateProjectDocument(parameters.TextDocument.Uri, updatedDocumentText);
            PublishDiagnostics(projectDocument);
        }

        /// <summary>
        ///     Called when a text document is saved.
        /// </summary>
        /// <param name="parameters">
        ///     The notification parameters.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task OnDidSaveTextDocument(DidSaveTextDocumentParams parameters)
        {
            Log.Information("Reloading project...");
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri, reload: true);
            PublishDiagnostics(projectDocument);

            if (!projectDocument.HasXml)
            {
                Log.Warning("Failed to reload project file {ProjectFilePath} (XML is invalid).", projectDocument.ProjectFile.FullName);

                return;
            }

            if (!projectDocument.HasMSBuildProject)
            {
                Log.Warning("Reloaded project file {ProjectFilePath} (XML is valid, but MSBuild project is not).", projectDocument.ProjectFile.FullName);

                return;
            }

            Log.Information("Successfully reloaded project {ProjectFilePath}.", projectDocument.ProjectFile.FullName);
        }

        /// <summary>
        ///     Called when a text document is opened.
        /// </summary>
        /// <param name="parameters">
        ///     The notification parameters.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task OnDidCloseTextDocument(DidCloseTextDocumentParams parameters)
        {
            if (_projectDocuments.TryRemove(parameters.TextDocument.Uri, out ProjectDocument projectDocument))
            {
                using (await projectDocument.Lock.WriterLockAsync())
                {
                    ClearDiagnostics(projectDocument);
                    projectDocument.Unload();
                }
            }
        }

        /// <summary>
        ///     Called when the mouse pointer hovers over text.
        /// </summary>
        /// <param name="parameters">
        ///     The notification parameters.
        /// </param>
        /// <param name="cancellationToken">
        ///     A <see cref="CancellationToken"/> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     A <see cref="Task{TResult}"/> whose result is the hover details, or <c>null</c> if no hover details are provided by the handler.
        /// </returns>
        protected override async Task<Hover> OnHover(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
        {
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri);
            
            using (await projectDocument.Lock.ReaderLockAsync(cancellationToken))
            {
                if (!projectDocument.HasXml)
                    return null;

                Position position = parameters.Position.ToNative();
                
                // Try to match up the position with an element or attribute in the XML, then match that up with an MSBuild object.
                SyntaxNode xmlAtPosition = projectDocument.GetXmlAtPosition(position);
                if (xmlAtPosition == null)
                    return null;

                // Match up the MSBuild item / property with its corresponding XML element / attribute.
                MSBuildObject msbuildObjectAtPosition = projectDocument.HasMSBuildProject ? projectDocument.GetMSBuildObjectAtPosition(position) : null;

                SyntaxNode elementOrAttribute = xmlAtPosition.GetContainingElementOrAttribute();
                if (elementOrAttribute == null)
                    return null;

                Range range = elementOrAttribute.Span.ToNative(projectDocument.XmlPositions);
                Hover result = new Hover
                {
                    Range = range.ToLsp()
                };

                if (elementOrAttribute is IXmlElementSyntax element)
                {
                    if (msbuildObjectAtPosition is MSBuildProperty propertyFromElementAtPosition)
                        result.Contents = $"**Property**: {propertyFromElementAtPosition.Name} = '{propertyFromElementAtPosition.Value}'";
                    else if (msbuildObjectAtPosition is MSBuildItem itemFromElementAtPosition)
                    {
                        if (itemFromElementAtPosition.Name == "PackageReference")
                        {
                            string packageVersion = itemFromElementAtPosition.GetMetadataValue("Version");
                            result.Contents = $"**NuGet Package**: {itemFromElementAtPosition.Include} v{packageVersion}";
                        }
                        else
                            result.Contents = $"**Item**: {element.Name.Name}({itemFromElementAtPosition.Include})";

                    }
                    else if (msbuildObjectAtPosition is MSBuildTarget targetFromElementAtPosition)
                        result.Contents = $"**Target**: {targetFromElementAtPosition.Name}";
                    else if (msbuildObjectAtPosition is MSBuildImport importFromElementAtPosition)
                        result.Contents = $"**Import**: {importFromElementAtPosition.ProjectImportElement.Project}";
                    else
                        return null;
                }
                else if (elementOrAttribute is XmlAttributeSyntax attribute)
                {
                    if (msbuildObjectAtPosition is MSBuildItem itemFromAttributeAtPosition && attribute.Name == "Sdk")
                    {
                        string metadataName = attribute.Name;
                        if (String.Equals(metadataName, "Include"))
                            metadataName = "Identity";

                        string metadataValue = itemFromAttributeAtPosition.GetMetadataValue(metadataName);
                        result.Contents = $"**Metadata**: {itemFromAttributeAtPosition.Name}({itemFromAttributeAtPosition.Include}).{metadataName} = '{metadataValue}'";
                    }
                    else if (msbuildObjectAtPosition is MSBuildSdkImport sdkImportFromAttribute)
                    {
                        StringBuilder importedProjectFiles = new StringBuilder();
                        foreach (string projectFile in sdkImportFromAttribute.ImportedProjectFiles)
                            importedProjectFiles.AppendLine($"* Imports [{Path.GetFileName(projectFile)}]({UriHelper.ToDocumentUri(projectFile)})");

                        result.Contents = new MarkedStringContainer(
                            $"**SDK Import**: {sdkImportFromAttribute.Name}",
                            importedProjectFiles.ToString()
                        );
                    }
                    else if (msbuildObjectAtPosition is MSBuildImport importFromAttribute && attribute.Name == "Project")
                    {
                        result.Contents = new MarkedStringContainer(
                            $"**Import**: {importFromAttribute.Name}",
                            $"* Imports [{Path.GetFileName(importFromAttribute.ImportedProjectRoot.FullPath)}]({UriHelper.ToDocumentUri(importFromAttribute.ImportedProjectRoot.FullPath)})"
                        );
                    }
                    else
                        return null;
                }
                else
                    return null;

                return result;
            }
        }

        /// <summary>
        ///     Called when completions are requested.
        /// </summary>
        /// <param name="parameters">
        ///     The request parameters.
        /// </param>
        /// <param name="cancellationToken">
        ///     A <see cref="CancellationToken"/> that can be used to cancel the request.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation whose result is the completion list or <c>null</c> if no completions are provided.
        /// </returns>
        protected override async Task<CompletionList> OnCompletion(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
        {
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri);

            List<CompletionItem> completionItems = new List<CompletionItem>();
            using (await projectDocument.Lock.ReaderLockAsync(cancellationToken))
            {
                if (!projectDocument.HasXml)
                    return null;

                Position position = parameters.Position.ToNative();

                // Try to match up the position with an element or attribute in the XML.
                SyntaxNode xmlAtPosition = projectDocument.GetXmlAtPosition(position);
                if (xmlAtPosition == null)
                    return null;

                // Are we on an attribute?
                XmlAttributeSyntax attributeAtPosition = xmlAtPosition.GetContainingAttribute();
                if (attributeAtPosition == null)
                    return null;

                // Must be a PackageReference element.
                if (!String.Equals(attributeAtPosition.ParentElement.Name, "PackageReference", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Are we on the attribute's value?
                Range attributeValueRange = attributeAtPosition.ValueNode.Span
                    .ToNative(projectDocument.XmlPositions)
                    .Transform( // Trim off leading and trailing quotes.
                        moveStartColumns: 1,
                        moveEndColumns: -1
                    );
                if (!attributeValueRange.Contains(position))
                    return null;

                try
                {
                    if (attributeAtPosition.Name == "Include")
                    {
                        string packageIdPrefix = attributeAtPosition.Value;
                        SortedSet<string> packageIds = await projectDocument.SuggestPackageIds(packageIdPrefix, cancellationToken);
                        
                        completionItems.AddRange(
                            packageIds.Select(packageId => new CompletionItem
                            {
                                Label = packageId,
                                Kind = CompletionItemKind.Module,
                                TextEdit = new TextEdit
                                {
                                    Range = attributeValueRange.ToLsp(),
                                    NewText = packageId
                                }
                            })
                        );
                    }
                    else if (attributeAtPosition.Name == "Version")
                    {
                        XmlAttributeSyntax includeAttribute = attributeAtPosition.ParentElement.AsSyntaxElement["Include"];
                        if (includeAttribute == null)
                            return null;

                        string packageId = includeAttribute.Value;
                        SortedSet<NuGetVersion> packageVersions = await projectDocument.SuggestPackageVersions(packageId, cancellationToken);
                        
                        completionItems.AddRange(
                            packageVersions.Select(packageVersion => new CompletionItem
                            {
                                Label = packageVersion.ToNormalizedString(),
                                Kind = CompletionItemKind.Field,
                                TextEdit = new TextEdit
                                {
                                    Range = attributeValueRange.ToLsp(),
                                    NewText = packageVersion.ToNormalizedString()
                                }
                            })
                        );
                    }
                    else
                        return null; // No completions.
                }
                catch (AggregateException aggregateSuggestionError)
                {
                    foreach (Exception suggestionError in aggregateSuggestionError.Flatten().InnerExceptions)
                    {
                        Log.Error(suggestionError, "Failed to provide completions.");
                    }
                }
                catch (Exception suggestionError)
                {
                    Log.Error(suggestionError, "Failed to provide completions.");
                }
            }

            CompletionList completionList = new CompletionList(completionItems,
                isIncomplete: completionItems.Count >= 20 // Default page size.
            );

            return completionList;
        }

        /// <summary>
        ///     Called when completions are requested.
        /// </summary>
        /// <param name="parameters">
        ///     The request parameters.
        /// </param>
        /// <param name="cancellationToken">
        ///     A <see cref="CancellationToken"/> that can be used to cancel the request.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation whose result is the completion list or <c>null</c> if no completions are provided.
        /// </returns>
        protected override async Task<SymbolInformationContainer> OnDocumentSymbols(DocumentSymbolParams parameters, CancellationToken cancellationToken)
        {
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri);

            List<SymbolInformation> symbols = new List<SymbolInformation>();
            using (await projectDocument.Lock.ReaderLockAsync(cancellationToken))
            {
                if (!projectDocument.HasMSBuildProject)
                    return null;

                foreach (MSBuildObject msbuildObject in projectDocument.MSBuildLookup.AllObjects)
                {
                    SymbolInformation symbol = new SymbolInformation
                    {
                        Name = msbuildObject.Name,
                        Location = new Location
                        {
                            Uri = projectDocument.DocumentUri,
                            Range = msbuildObject.XmlRange.ToLsp()
                        }
                    };
                    if (msbuildObject is MSBuildItem item)
                    {
                        symbol.ContainerName = "Item";
                        symbol.Name = $"{item.Name} ({item.Include})";
                        symbol.Kind = SymbolKind.Array;
                    }
                    else if (msbuildObject is MSBuildTarget)
                    {
                        symbol.ContainerName = "Target";
                        symbol.Kind = SymbolKind.Function;
                    }
                    else if (msbuildObject is MSBuildProperty)
                    {
                        symbol.ContainerName = "Property";
                        symbol.Kind = SymbolKind.Property;
                    }
                    else if (msbuildObject is MSBuildImport)
                    {
                        symbol.ContainerName = "Import";
                        symbol.Kind = SymbolKind.Package;
                    }
                    else if (msbuildObject is MSBuildSdkImport)
                    {
                        symbol.ContainerName = "Import (SDK)";
                        symbol.Kind = SymbolKind.Package;
                    }
                    else
                        continue;

                    symbols.Add(symbol);
                }
            }

            if (symbols.Count == 0)
                return null;

            return new SymbolInformationContainer(
                symbols.OrderBy(symbol => symbol.Name)
            );
        }

        /// <summary>
        ///     Called when a definition is requested.
        /// </summary>
        /// <param name="parameters">
        ///     The request parameters.
        /// </param>
        /// <param name="cancellationToken">
        ///     A <see cref="CancellationToken"/> that can be used to cancel the request.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation whose result is the definition location or <c>null</c> if no definition is provided.
        /// </returns>
        protected override async Task<LocationOrLocations> OnDefinition(TextDocumentPositionParams parameters, CancellationToken cancellationToken)
        {
            ProjectDocument projectDocument = await GetProjectDocument(parameters.TextDocument.Uri);

            using (await projectDocument.Lock.ReaderLockAsync(cancellationToken))
            {
                if (!projectDocument.HasMSBuildProject)
                    return null;

                Position position = parameters.Position.ToNative();
                MSBuildObject msbuildObjectAtPosition = projectDocument.GetMSBuildObjectAtPosition(position);
                if (msbuildObjectAtPosition == null)
                    return null;

                if (msbuildObjectAtPosition is MSBuildSdkImport import)
                {
                    if (msbuildObjectAtPosition is MSBuildSdkImport sdkImportAtPosition)
                    {
                        // TODO: Parse imported project and determine location of root element (use that range instead).
                        Location[] locations =
                            sdkImportAtPosition.ImportedProjectRoots.Select(
                                importedProjectRoot => new Location
                                {
                                    Range = Range.Empty.ToLsp(),
                                    Uri = UriHelper.ToDocumentUri(importedProjectRoot.Location.File)
                                }
                            )
                            .ToArray();

                        return new LocationOrLocations(locations);
                    }
                    else if (msbuildObjectAtPosition is MSBuildImport importAtPosition)
                    {
                        // TODO: Parse imported project and determine location of root element (use that range instead).
                        return new LocationOrLocations(new Location
                        {
                            Range = Range.Empty.ToLsp(),
                            Uri = UriHelper.ToDocumentUri(importAtPosition.ImportedProjectRoot.Location.File)
                        });
                    }
                }
            }

            return null;
        }

        /// <summary>
        ///     Try to retrieve the current state for the specified project document.
        /// </summary>
        /// <param name="documentUri">
        ///     The project document URI.
        /// </param>
        /// <param name="reload">
        ///     Reload the project if it is already loaded?
        /// </param>
        /// <returns>
        ///     The project document.
        /// </returns>
        async Task<ProjectDocument> GetProjectDocument(Uri documentUri, bool reload = false)
        {
            string projectFilePath = documentUri.GetFileSystemPath();

            bool isNewProject = false;
            ProjectDocument projectDocument = _projectDocuments.GetOrAdd(documentUri, _ =>
            {
                isNewProject = true;

                return new ProjectDocument(documentUri, Log);
            });

            try
            {
                if (isNewProject || reload)
                {
                    using (await projectDocument.Lock.WriterLockAsync())
                    {
                        await projectDocument.Load();
                    }
                }
            }
            catch (XmlException invalidXml)
            {
                Log.Error("Error parsing project file {ProjectFilePath}: {ErrorMessage:l}",
                    projectFilePath,
                    invalidXml.Message
                );
            }
            catch (Exception loadError)
            {
                Log.Error(loadError, "Unexpected error loading file {ProjectFilePath}.", projectFilePath);
            }

            return projectDocument;
        }

        /// <summary>
        ///     Try to retrieve the current state for the specified project document.
        /// </summary>
        /// <param name="documentUri">
        ///     The project document URI.
        /// </param>
        /// <param name="reload">
        ///     Reload the project if it is already loaded?
        /// </param>
        /// <returns>
        ///     The project document.
        /// </returns>
        async Task<ProjectDocument> TryUpdateProjectDocument(Uri documentUri, string documentText)
        {
            ProjectDocument projectDocument;
            if (!_projectDocuments.TryGetValue(documentUri, out projectDocument))
            {
                Log.Error("Tried to update non-existent project with document URI {DocumentUri}.", documentUri);

                throw new InvalidOperationException($"Project with document URI '{documentUri}' is not loaded.");
            }

            try
            {
                using (await projectDocument.Lock.WriterLockAsync())
                {
                    projectDocument.Update(documentText);
                }
            }
            catch (Exception updateError)
            {
                Log.Error(updateError, "Failed to update project {ProjectFile}.", projectDocument.ProjectFile.FullName);
            }

            return projectDocument;
        }

        /// <summary>
        ///     Publish current diagnostics (if any) for the specified project document.
        /// </summary>
        /// <param name="projectDocument">
        ///     The project document.
        /// </param>
        void PublishDiagnostics(ProjectDocument projectDocument)
        {
            if (projectDocument == null)
                throw new ArgumentNullException(nameof(projectDocument));

            Server.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = projectDocument.DocumentUri,
                Diagnostics = projectDocument.Diagnostics.ToArray()
            });   
        }

        /// <summary>
        ///     Clear current diagnostics (if any) for the specified project document.
        /// </summary>
        /// <param name="projectDocument">
        ///     The project document.
        /// </param>
        void ClearDiagnostics(ProjectDocument projectDocument)
        {
            if (projectDocument == null)
                throw new ArgumentNullException(nameof(projectDocument));

            if (!projectDocument.HasDiagnostics)
                return;

            Server.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = projectDocument.DocumentUri,
                Diagnostics = new Lsp.Models.Diagnostic[0] // Overwrites existing diagnostics for this document with an empty list
            });
            
        }
    }
}
