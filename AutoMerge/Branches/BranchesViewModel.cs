﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AutoMerge.Events;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Prism.Events;
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;

using TeamExplorerSectionViewModelBase = AutoMerge.Base.TeamExplorerSectionViewModelBase;

namespace AutoMerge
{
	public class BranchesViewModel : TeamExplorerSectionViewModelBase
	{
		private enum MergeResult
		{
			Success,
			NothingMerge,
			CheckInFail,
			CheckInEvaluateFail,
			UnresolvedConflicts,
			PartialSuccess
		}

		private enum CheckInResult
		{
			Success,
			NothingMerge,
			CheckInFail,
			CheckInEvaluateFail
		}

		private readonly IEventAggregator _eventAggregator;

		private ChangesetViewModel _changeset;

		/// <summary>
		/// Constructor.
		/// </summary>
		public BranchesViewModel()
		{
			Title = Resources.BrancheSectionName;
			IsVisible = true;
			IsExpanded = true;
			IsBusy = false;

			MergeCommand = new DelegateCommand(MergeExecute, MergeCanEcexute);

			_eventAggregator = EventAggregatorFactory.Get();
		}

		/// <summary>
		/// Get the view.
		/// </summary>
		protected BranchesView View
		{
			get { return SectionContent as BranchesView; }
		}

		public ObservableCollection<MergeInfoViewModel> Branches
		{
			get
			{
				return _branches;
			}
			set
			{
				_branches = value;
				RaisePropertyChanged("Branches");
			}
		}
		private ObservableCollection<MergeInfoViewModel> _branches;

		public DelegateCommand MergeCommand { get; private set; }

		public MergeOption MergeOption
		{
			get { return _mergeOption; }
			set 
			{	_mergeOption = value;
				RaisePropertyChanged("MergeOption");
			}
		}
		private MergeOption _mergeOption;

		public async override void Initialize(object sender, SectionInitializeEventArgs e)
		{
			base.Initialize(sender, e);
			await RefreshAsync();

			_eventAggregator.GetEvent<SelectChangesetEvent>()
				.Subscribe(OnSelectedChangeset);
			_eventAggregator.GetEvent<BranchSelectedChangedEvent>()
				.Subscribe(OnBranchSelectedChanged);
		}

		private void OnBranchSelectedChanged(MergeInfoViewModel obj)
		{
			MergeCommand.RaiseCanExecuteChanged();
		}

		/// <summary>
		/// Refresh override.
		/// </summary>
		public async override void Refresh()
		{
			base.Refresh();
			await RefreshAsync();
		}

		/// <summary>
		/// Refresh the changeset data asynchronously.
		/// </summary>
		protected override async Task RefreshAsync()
		{
			var changeset = _changeset;
			try
			{
				// Set our busy flag and clear the previous data
				IsBusy = true;
				if (changeset == null || !changeset.CanMerge)
				{
					Branches = new ObservableCollection<MergeInfoViewModel>();
					return;
				}

				var branches = await Task.Run(() => GetBranches(changeset.ChangesetId));

				// Selected changeset in sequence
				if (changeset.ChangesetId == _changeset.ChangesetId)
				{
					Branches = branches;
					MergeCommand.RaiseCanExecuteChanged();
				}

			}
			catch (Exception ex)
			{
				ShowNotification(ex.Message, NotificationType.Error);
			}
			finally
			{
				IsBusy = false;
			}
		}

		private void OnSelectedChangeset(ChangesetViewModel changeset)
		{
			_changeset = changeset;
			Refresh();
		}

		private ObservableCollection<MergeInfoViewModel> GetBranches(int changesetId)
		{
			var context = VersionControlNavigationHelper.GetContext(ServiceProvider);
			var tfs = context.TeamProjectCollection;
			var versionControl = tfs.GetService<VersionControlServer>();

			var sourceBranches = versionControl.QueryBranchObjectOwnership(new[] { changesetId });

			var result = new ObservableCollection<MergeInfoViewModel>();
			if (sourceBranches.Length == 0)
				return result;

			var workspace = versionControl.QueryWorkspaces(null, tfs.AuthorizedIdentity.UniqueName, Environment.MachineName)[0];

			var changesetService = new ChangesetService(versionControl, context.TeamProjectName);
			var sourceBranchIdentifier = changesetService.GetAssociatedBranches(changesetId)[0];
			var sourceBranchInfo = versionControl.QueryBranchObjects(sourceBranchIdentifier, RecursionType.None)[0];

			var changeset = changesetService.GetChangeset(changesetId);

			if (sourceBranchInfo.Properties != null && sourceBranchInfo.Properties.ParentBranch != null
				&& !sourceBranchInfo.Properties.ParentBranch.IsDeleted)
			{
				var parentBranch = sourceBranchInfo.Properties.ParentBranch.Item;
				var mergeInfo = new MergeInfoViewModel(_eventAggregator)
				{
					SourceBranch = sourceBranchIdentifier.Item,
					TargetBranch = parentBranch,
					ValidationResult =
						ValidateBranch(workspace, sourceBranchIdentifier.Item, parentBranch, changeset.Changes, changeset.ChangesetId),
				};

				mergeInfo.Checked = mergeInfo.ValidationResult == BranchValidationResult.Success;

				result.Add(mergeInfo);
			}

			var currentBranchInfo = new MergeInfoViewModel(_eventAggregator)
			{
				SourceBranch = sourceBranchIdentifier.Item,
				TargetBranch = sourceBranchIdentifier.Item,
				ValidationResult = BranchValidationResult.Success
			};
			result.Add(currentBranchInfo);

			if (sourceBranchInfo.ChildBranches != null)
			{
				var childBranches = sourceBranchInfo.ChildBranches.Where(b => !b.IsDeleted)
					.Reverse();
				foreach (var childBranch in childBranches)
				{
					var mergeInfo = new MergeInfoViewModel(_eventAggregator)
					{
						SourceBranch = sourceBranchIdentifier.Item,
						TargetBranch = childBranch.Item
					};

					mergeInfo.ValidationResult = ValidateBranch(workspace, sourceBranchIdentifier.Item, childBranch.Item, changeset.Changes, changeset.ChangesetId);

					result.Add(mergeInfo);
				}
			}

			return result;
		}

		private static BranchValidationResult ValidateBranch(Workspace workspace, string sourceBranch, string targetBranch, Change[] changes, int changesetId)
		{
			var result = BranchValidationResult.Success;
			if (result == BranchValidationResult.Success)
			{
				var isMapped = IsMapped(workspace, sourceBranch, targetBranch, changes);
				if (!isMapped)
					result = BranchValidationResult.BranchNotMapped;
			}

			if (result == BranchValidationResult.Success)
			{
				var hasLocalChanges = HaskLocalChanges(workspace, sourceBranch, targetBranch, changes);
				if (hasLocalChanges)
					result = BranchValidationResult.ItemHasLocalChanges;
			}

			if (result == BranchValidationResult.Success)
			{
				var isMerge = IsMerged(workspace.VersionControlServer, sourceBranch, targetBranch, changes, changesetId);
				if (isMerge)
					result = BranchValidationResult.AlreadyMerged;
			}

			return result;
		}

		private static bool IsMerged(VersionControlServer versionControlServer, string sourceBranch, string targetBranch, IEnumerable<Change> changes, int changesetId)
		{
			foreach (var change in changes)
			{
				var source = change.Item.ServerItem;
				var target = source.Replace(sourceBranch, targetBranch);

				var mergeCandidates = versionControlServer.GetMergeCandidates(new ItemSpec(source, RecursionType.None), target);
				if (mergeCandidates.Any(m => m.Changeset.ChangesetId == changesetId))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsMapped(Workspace workspace, string sourceBranch, string targetBranch, IEnumerable<Change> changes)
		{
			var targetItems = changes
				.Select(c => c.Item.ServerItem)
				.Select(path => path.Replace(sourceBranch, targetBranch));

			foreach (var targetItem in targetItems)
			{
				var workingFolder = workspace.TryGetWorkingFolderForServerItem(targetItem);
				if (workingFolder == null)
					return false;
			}

			return true;
		}

		private static bool HaskLocalChanges(Workspace workspace, string sourceBranch, string targetBranch, IEnumerable<Change> changes)
		{
			var itemSpecs = changes
				.Select(c => c.Item.ServerItem)
				.Select(path => path.Replace(sourceBranch, targetBranch))
				.Select(path => new ItemSpec(path, RecursionType.None))
				.ToArray();

			var pendingChanges = workspace.GetPendingChangesEnumerable(itemSpecs);

			return pendingChanges.Any();
		}

		public async void MergeExecute()
		{
			try
			{
				IsBusy = true;

				var result = await Task.Run(() =>MergeExecuteInternal());

				switch (result)
				{
					case MergeResult.CheckInEvaluateFail:
						ShowNotification("Check In evaluate failed", NotificationType.Error);
						break;
					case MergeResult.CheckInFail:
						ShowNotification("Check In failed", NotificationType.Error);
						break;
					case MergeResult.NothingMerge:
						ShowNotification("Nothing merged", NotificationType.Warning);
						break;
					case MergeResult.PartialSuccess:
						ShowNotification("Partial success", NotificationType.Error);
						break;
					case MergeResult.UnresolvedConflicts:
						ShowNotification("Unresolved conflicts", NotificationType.Error);
						break;
					case MergeResult.Success:
						ShowNotification("Merge is successful", NotificationType.Information);
						break;
				}
				_eventAggregator.GetEvent<MergeCompleteEvent>().Publish(true);
			}
			catch (Exception ex)
			{
				ShowNotification(ex.Message, NotificationType.Error);
			}
			finally
			{
				IsBusy = false;
			}
		}

		private MergeResult MergeExecuteInternal()
		{
			var result = MergeResult.NothingMerge;
			var context = VersionControlNavigationHelper.GetContext(ServiceProvider);
			var tfs = context.TeamProjectCollection;
			var versionControl = tfs.GetService<VersionControlServer>();
			
			var workspace = versionControl.QueryWorkspaces(null, tfs.AuthorizedIdentity.UniqueName, Environment.MachineName)[0];

			var changesetService = new ChangesetService(versionControl, context.TeamProjectName);
			var changeset = changesetService.GetChangeset(_changeset.ChangesetId);
			var mergeOption = _mergeOption;
			var workItemStore = tfs.GetService<WorkItemStore>();
			var versionSpec = new ChangesetVersionSpec(changeset.ChangesetId);

			var sourceChanges = changeset.Changes;
			foreach (var mergeInfo in _branches.Where(b => b.Checked))
			{
				List<PendingChange> targetPendingChanges;
				if (!MergeToBranch(mergeInfo.SourceBranch, mergeInfo.TargetBranch, sourceChanges, versionSpec, mergeOption, workspace, out targetPendingChanges))
				{
					return result == MergeResult.Success ? MergeResult.PartialSuccess : MergeResult.UnresolvedConflicts;
				}

				// Another user can update workitem. Need re-read before update.
				// TODO: maybe move to workspace.CheckIn operation
				var workItems = GetWorkItemCheckinInfo(changeset, workItemStore);
				var checkInResult = CheckIn(targetPendingChanges, mergeInfo, workspace, workItems, changeset.Comment, changeset.PolicyOverride);
				switch (checkInResult)
				{
					case CheckInResult.CheckInEvaluateFail:
						return MergeResult.CheckInEvaluateFail;
					case CheckInResult.CheckInFail:
						return result == MergeResult.Success ? MergeResult.PartialSuccess : MergeResult.CheckInFail;
					case CheckInResult.Success:
						result = MergeResult.Success;
						break;
				}
			}

			return result;
		}

		private static CheckInResult CheckIn(IReadOnlyCollection<PendingChange> targetPendingChanges, MergeInfoViewModel mergeInfoView,
			Workspace workspace, WorkItemCheckinInfo[] workItems, string sourceComment, PolicyOverrideInfo policyOverride)
		{
			if (targetPendingChanges.Count == 0)
				return CheckInResult.NothingMerge;

			var comment = EvaluateComment(sourceComment, mergeInfoView.SourceBranch, mergeInfoView.TargetBranch);
			var evaluateCheckIn = workspace.EvaluateCheckin2(CheckinEvaluationOptions.All,
				targetPendingChanges,
				comment,
				null,
				workItems);

			var skipPolicyValidate = !policyOverride.PolicyFailures.IsNullOrEmpty();
			if (!CanCheckIn(evaluateCheckIn, skipPolicyValidate))
				return CheckInResult.CheckInEvaluateFail;

			//var changesetId = 1;
			var changesetId = workspace.CheckIn(targetPendingChanges.ToArray(), null, comment,
				null, workItems, policyOverride);
			return changesetId <= 0 ? CheckInResult.CheckInFail : CheckInResult.Success;
		}

		private static bool MergeToBranch(string sourceBranch, string targetBranch, IEnumerable<Change> sourceChanges, VersionSpec version, MergeOption mergeOption,
			Workspace workspace, out List<PendingChange> targetPendingChanges)
		{
			var conflicts = new List<string>();
			var allTargetsFiles = new HashSet<string>();
			targetPendingChanges = null;
			foreach (var change in sourceChanges)
			{
				var source = change.Item.ServerItem;
				var target = source.Replace(sourceBranch, targetBranch);
				allTargetsFiles.Add(target);

				var getLatestResult = workspace.Get(new[] {target}, VersionSpec.Latest, RecursionType.None, GetOptions.None);
				if (!getLatestResult.NoActionNeeded)
				{
					// HACK.
					getLatestResult = workspace.Get(new[] { target }, VersionSpec.Latest, RecursionType.None, GetOptions.None);
					if (!getLatestResult.NoActionNeeded)
						return false;
				}

				var mergeOptions = ToTfsMergeOptions(mergeOption);
				var status = workspace.Merge(source, target, version, version, LockLevel.None, RecursionType.None, mergeOptions);

				if (HasConflicts(status))
				{
					conflicts.Add(target);
				}
			}

			if (conflicts.Count > 0)
			{
				var resolved = ResolveConflict(workspace, conflicts.ToArray());
				if (!resolved)
				{
					return false;
				}
			}

			var allPendingChanges = workspace.GetPendingChangesEnumerable();
			targetPendingChanges = allPendingChanges
				.Where(pendingChange => allTargetsFiles.Contains(pendingChange.ServerItem))
				.ToList();

			return true;
		}

		private static MergeOptions ToTfsMergeOptions(MergeOption mergeOption)
		{
			switch (mergeOption)
			{
				case MergeOption.KeepTarget:
					return MergeOptions.AlwaysAcceptMine;
				case MergeOption.OverwriteTarget:
					return MergeOptions.ForceMerge;
				case MergeOption.ManualResolveConflict:
					return MergeOptions.None;
				default:
					return MergeOptions.None;
			}
		}

		public bool MergeCanEcexute()
		{
			return _branches.Any(b => b.Checked);
		}

		private static bool HasConflicts(GetStatus mergeStatus)
		{
			return !mergeStatus.NoActionNeeded && mergeStatus.NumConflicts > 0;
		}

		private static bool ResolveConflict(Workspace workspace, string[] targetPath)
		{
			var conflicts = workspace.QueryConflicts(targetPath, false);
			if (conflicts.IsNullOrEmpty())
				return true;

			workspace.AutoResolveValidConflicts(conflicts, AutoResolveOptions.AllSilent);

			conflicts = workspace.QueryConflicts(targetPath, false);
			if (conflicts.IsNullOrEmpty())
				return true;

			foreach (var conflict in conflicts)
			{
				if (workspace.MergeContent(conflict, true))
				{
					conflict.Resolution = Resolution.AcceptMerge;
					workspace.ResolveConflict(conflict);
				}
				if (!conflict.IsResolved)
				{
					return false;
				}
			}

			return true;
		}

		private static string EvaluateComment(string sourceComment, string sourceBranch, string targetBranch)
		{
			if (string.IsNullOrWhiteSpace(sourceComment))
				return null;

			var targetShortBranchName = GetShortBranchName(targetBranch);
			string comment;
			if (sourceComment.StartsWith("MERGE "))
			{
				var originalCommentStartPos = sourceComment.IndexOf('(');
				var mergeComment = sourceComment.Substring(0, originalCommentStartPos);
				var originaComment =
					originalCommentStartPos + 1 < sourceComment.Length
					? sourceComment.Substring(originalCommentStartPos + 1, sourceComment.Length - originalCommentStartPos - 2)
					: string.Empty;
				comment = string.Format("{0} -> {1} ({2})", mergeComment, targetShortBranchName, originaComment);
			}
			else
			{
				var sourceShortBranchName = GetShortBranchName(sourceBranch);
				comment = string.Format("MERGE {0} -> {1} ({2})", sourceShortBranchName, targetShortBranchName, sourceComment);
			}

			return comment;
		}

		private static string GetShortBranchName(string fullBranchName)
		{
			var pos = fullBranchName.LastIndexOf('/');
			var shortName = fullBranchName.Substring(pos + 1);
			return shortName;
		}

		private static WorkItemCheckinInfo[] GetWorkItemCheckinInfo(Changeset changeset, WorkItemStore workItemStore)
		{
			if (changeset.WorkItems == null)
				return null;

			var result = new List<WorkItemCheckinInfo>(changeset.WorkItems.Length);
			foreach (var associatedWorkItem in changeset.AssociatedWorkItems)
			{
				var workItem = workItemStore.GetWorkItem(associatedWorkItem.Id);
				var workItemCheckinInfo = new WorkItemCheckinInfo(workItem, WorkItemCheckinAction.Associate);
				result.Add(workItemCheckinInfo);
			}

			return result.ToArray();
		}

		private static bool CanCheckIn(CheckinEvaluationResult checkinEvaluationResult, bool skipPolicy)
		{
			var result = checkinEvaluationResult.Conflicts.IsNullOrEmpty()
				&& checkinEvaluationResult.NoteFailures.IsNullOrEmpty()
				&& checkinEvaluationResult.PolicyEvaluationException == null;

			if (!skipPolicy)
				result &= checkinEvaluationResult.PolicyFailures.IsNullOrEmpty();
			return result;
		}
	}
}