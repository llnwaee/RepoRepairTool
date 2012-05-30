﻿using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Windows;
using GitHub;
using GitHub.Helpers;
using Ninject;
using ReactiveUI;
using ReactiveUI.Routing;
using ReactiveUI.Xaml;
using GitHub.Helpers;
using RepoRepairTool.Helpers;

namespace RepoRepairTool.ViewModels
{
    public interface IDropRepoViewModel : IRoutableViewModel
    {
        string CurrentRepoPath { get; }

        ReactiveAsyncCommand AnalyzeRepo { get; }
        ReactiveCollection<IBranchInformationViewModel> BranchInformation { get; }
            
        Visibility RepairButtonVisibility { get; }
        ReactiveCommand RepairButton { get; }
    }

    public interface IBranchInformationViewModel : IReactiveNotifyPropertyChanged
    {
        string BranchName { get; }
        HeuristicTreeInformation Model { get; }
        Visibility NeedsRepair { get; }

        string BadEncodingInfoHeader { get; }
        string BadEndingsInfoHeader { get; }
    }

    public class DropRepoViewModel : ReactiveObject, IDropRepoViewModel
    {
        public ReactiveAsyncCommand AnalyzeRepo { get; protected set; }

        ObservableAsPropertyHelper<string> _CurrentRepoPath;
        public string CurrentRepoPath { get { return _CurrentRepoPath.Value; } }

        ObservableAsPropertyHelper<ReactiveCollection<IBranchInformationViewModel>> _BranchInformation;
        public ReactiveCollection<IBranchInformationViewModel> BranchInformation { get { return _BranchInformation.Value; } }

        ObservableAsPropertyHelper<Visibility> _RepairButtonVisibility;
        public Visibility RepairButtonVisibility { get { return _RepairButtonVisibility.Value; } }

        public ReactiveCommand RepairButton { get; protected set; }

        public string UrlPathSegment {
            get { return "drop"; }
        }

        public IScreen HostScreen { get; protected set; }

        public DropRepoViewModel(IScreen hostScreen, IAppState appState, IRepoAnalysisProvider analyzeFunc)
        {
            HostScreen = hostScreen;

            AnalyzeRepo = new ReactiveAsyncCommand();

            CoreUtility.ExtractLibGit2();

            var scanResult = AnalyzeRepo.RegisterAsyncObservable(x => 
                analyzeFunc.AnalyzeRepo((string) x).Catch<RepoAnalysisResult, Exception>(ex => {
                    this.Log().WarnException("Failed to analyze repo", ex);

                    // XXX: This error message is derpy
                    UserError.Throw("Couldn't analyze repo", ex);
                    return Observable.Empty<RepoAnalysisResult>();
                }));

            scanResult.Select(x => x.RepositoryPath).ToProperty(this, x => x.CurrentRepoPath);
            scanResult
                .Select(x => x.BranchAnalysisResults.Select(y => (IBranchInformationViewModel)new BranchInformationViewModel(y.Key, y.Value)))
                .Select(x => new ReactiveCollection<IBranchInformationViewModel>(x))
                .ToProperty(this, x => x.BranchInformation);

            this.WhenAny(x => x.BranchInformation, x => x.Value != null ? Visibility.Visible : Visibility.Hidden)
                .ToProperty(this, x => x.RepairButtonVisibility);

            RepairButton = new ReactiveCommand();
            RepairButton.Subscribe(_ => {
                appState.BranchInformation = BranchInformation.Where(x => x.BranchName != Constants.WorkingDirectory).ToArray();
                appState.WorkingDirectoryInformation = BranchInformation.First(x => x.BranchName == Constants.WorkingDirectory).Model;
                appState.CurrentRepo = CurrentRepoPath;

                HostScreen.Router.Navigate.Execute(RxApp.GetService<IRepairViewModel>());
            });

            var viewStates = Observable.Merge(
                AnalyzeRepo.ItemsInflight.Where(x => x > 0).Select(_ => "Analyzing"),
                scanResult.Select(_ => "RepoAdded"));

            MessageBus.Current.RegisterMessageSource(viewStates, "DropRepoViewState");

            this.WhenNavigatedTo(() =>
                MessageBus.Current.Listen<string>("DropFolder").Subscribe(path => AnalyzeRepo.Execute(path)));
        }
    }

    public class BranchInformationViewModel : ReactiveObject, IBranchInformationViewModel
    {
        [DataMember]
        public string BranchName { get; protected set; }
        [DataMember]
        public HeuristicTreeInformation Model { get; protected set; }
        [DataMember]
        public Visibility NeedsRepair { get; protected set; }

        [DataMember]
        public string BadEncodingInfoHeader { get; protected set; }
        [DataMember]
        public string BadEndingsInfoHeader { get; protected set; }

        public BranchInformationViewModel(string branchName, HeuristicTreeInformation treeInformation)
        {
            BranchName = branchName;
            Model = treeInformation;

            if (Model.BadEncodingFiles == null || Model.BadLineEndingFiles == null) {
                NeedsRepair = Visibility.Collapsed;
                BadEncodingInfoHeader = "Unknown number of files incorrectly encoded";
                BadEndingsInfoHeader = "Unknown number of files with incorrect line endings";
                return;
            }

            if (Model.TotalFilesExamined == 0 || Model.LineEndingType == LineEndingType.Unsure) {
                NeedsRepair = Visibility.Collapsed;
                BadEncodingInfoHeader = "No text files found";
                BadEndingsInfoHeader = "No text files found";
                return;
            }

            // > 5% mixed line endings or any UTF-16 files => needs repair
            bool shouldRepair = Model.BadEncodingFiles.Count > 0 ||
                (double) Model.BadLineEndingFiles.Count / Model.TotalFilesExamined > 0.05;

            NeedsRepair = shouldRepair ? Visibility.Visible : Visibility.Collapsed;

            BadEncodingInfoHeader = Model.BadEncodingFiles.Count > 0 ?
                String.Format("{0:P2} of files are not in UTF-8 encoding", (double)Model.BadEncodingFiles.Count / Model.TotalFilesExamined) :
                "All of the files are correctly encoded";

            BadEndingsInfoHeader = Model.BadLineEndingFiles.Count > 0 ?
                String.Format("{0:P2} of files have a different line ending type than the repo", (double)Model.BadLineEndingFiles.Count / Model.TotalFilesExamined) :
                "All of the files have correct line endings";
        }
    }
}