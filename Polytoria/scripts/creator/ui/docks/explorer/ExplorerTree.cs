// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Shared;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Script = Polytoria.Datamodel.Script;
using Polytoria.Utils;
using System.Threading.Tasks;

namespace Polytoria.Creator.UI;

public partial class ExplorerTree : Tree
{
	public ExplorerItemContextMenu? ItemContextMenu;
	public World Root = null!;
	public readonly Dictionary<Instance, TreeItem> InstanceToItem = [];
	public readonly Dictionary<TreeItem, Instance> ItemToInstance = [];
	public TreeItem? ScrollToTarget = null!;

	public override void _Ready()
	{
		ItemActivated += OnItemActivated;
		base._Ready();
	}

	public override void _Process(double delta)
	{
		if (ScrollToTarget != null)
		{
			if (GodotObject.IsInstanceValid(ScrollToTarget))
			{
				ScrollToItem(ScrollToTarget);
			}
			ScrollToTarget = null;
		}
		base._Process(delta);
	}

	/// <summary>
	/// Basically scroll to item but wait for next frame
	/// </summary>
	/// <param name="target"></param>
	public void ScrollToItemFrame(TreeItem target)
	{
		ScrollToTarget = target;
	}

	public override async void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseEvent)
		{
			if (mouseEvent is { ButtonIndex: MouseButton.Right, Pressed: true })
			{
				TreeItem clickedItem = GetItemAtPosition(mouseEvent.Position);
				if (clickedItem != null)
				{
					ItemContextMenu?.Close();

					// This is needed because selected instances couldn't update beforehand (especially with RMB select)
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

					List<Instance> instances = World.Current!.CreatorContext.Selections.SelectedInstances;

					if (instances.Count == 1)
					{
						instances.Clear();
						instances.Add(Explorer.GetInstanceFromTreeItem(clickedItem)!);
					}

					ItemContextMenu = new() { Targets = instances };
					AddChild(ItemContextMenu);
					ItemContextMenu.PopupAtCursor();
				}
			}
		}
		else if (@event.IsActionPressed("rename"))
		{
			AcceptEvent();
			EditSelected(true);
		}
		base._GuiInput(@event);
	}

	private void OnItemActivated()
	{
		TreeItem target = GetSelected();

		if (target == null)
		{
			return;
		}

		Instance clickedInstance = ItemToInstance[target];

		if (clickedInstance != null && clickedInstance is Datamodel.Script script)
		{
			CreatorService.OpenScript(script);
		}
	}

	public override Variant _GetDragData(Vector2 atPosition)
	{
		return new InstanceDragData()
		{
			Instances = [.. ItemToInstance
			.Where(kvp => IsInstanceValid(kvp.Key) && kvp.Key.IsSelected(0))
			.Select(kvp => kvp.Value)]
		}.Serialize();
	}

	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		DropModeFlags = (int)(DropModeFlagsEnum.OnItem | DropModeFlagsEnum.Inbetween);

		return true;
	}

	// Creates a script instance bound to a script file.
	private Script CreateScript(ScriptTypeEnum scriptType, string file, string name, Instance target)
	{
		Script script;

		if (scriptType == ScriptTypeEnum.Server)
		{
			script = Root.New<ServerScript>();
		}
		else if (scriptType == ScriptTypeEnum.Client)
		{
			script = Root.New<ClientScript>();
		}
		else
		{
			script = Root.New<ModuleScript>();
		}

		script.LinkedScript = Root.Assets.GetFileLinkByPath(file);
		script.Name = name;

		script.Parent = target;

		return script;
	}

	// Recursivly Inserts files using the standard Luau directory system.
	// Folders with an init.luau child become script instances instead of folders.
	// The Scripts type is determined by the suffix (server, client)
	private Instance? InsertFile(string file, Instance target)
	{
		// Make sure folder paths dont end with a '/'
		// Ex: "scripts/" turns into "scripts"
		file = file.TrimEnd('/', '\\');

		string fileExtension = file.GetExtension();

		if (Globals.ScriptFileExtensions.Contains(fileExtension))
		{
			ScriptTypeEnum scriptType = CreatorService.GetScriptTypeFromPath(file);
			string name = CreatorService.GetScriptNameFromPath(file);

			Script script = CreateScript(scriptType, file, name, target);
			return script;
		}
		else if (fileExtension == Globals.ModelFileExtension)
		{
			_ = Root.LinkedSession.InsertModel(file, target);
			return null;
		}
		else // Folder
		{
			string fileFullPath = Path.Combine(Root.LinkedSession.ProjectFolderPath, file);

			string[] childFiles = Directory.GetFiles(fileFullPath);
			string[] childDirectories = Directory.GetDirectories(fileFullPath);

			// Loops through files inside folder to find init.luau if it exists
			string? initFile = null;
			foreach (string childFullPath in childFiles)
			{
				string childFullName = Path.GetFileName(childFullPath);
				string childPath = Path.Combine(file, childFullName);
				if (childFullName == "init.luau")
				{
					initFile = childPath;
					break;
				}
			}

			Instance instance;

			string folderName = Path.GetFileNameWithoutExtension(file);

			if (initFile != null)
			{
				// Get script type from folder suffix to determine the script type.
				// add .luau extention so function doesnt error.
				ScriptTypeEnum scriptType = CreatorService.GetScriptTypeFromPath(file + ".luau");

				instance = CreateScript(scriptType, initFile, folderName, target);
			}
			else
			{
				Folder folder = Root.New<Folder>();
				folder.Name = folderName;
				folder.Parent = target;

				instance = folder;
			}

			// Recursivly call InsertFile on any directories (folders) inside the current folder.
			foreach (string fullChildPath in childDirectories)
			{
				string childFullName = Path.GetFileName(fullChildPath);
				string childPath = Path.Combine(file, childFullName).SanitizePath();

				InsertFile(childPath, instance);
			}

			// Recursivly call InsertFile on any files inside the current folder,
			// while skipping any invalid files
			foreach (string fullChildPath in childFiles)
			{
				// We shouldn't add any metadata files.
				string extension = Path.GetExtension(fullChildPath);
				if (extension == ".meta")
				{
					continue;
				}

				string childFullName = Path.GetFileName(fullChildPath);

				// We can skip over any init scripts
				if (childFullName == "init.luau")
				{
					continue;
				}

				string childPath = Path.Combine(file, childFullName).SanitizePath();

				_ = InsertFile(childPath, instance);
			}

			return instance;
		}
	}

	public override void _DropData(Vector2 atPosition, Variant data)
	{
		CreatorHistory history = Root.CreatorContext.History;
		TreeItem targetItem = GetItemAtPosition(atPosition);
		int dropSection = GetDropSectionAtPosition(atPosition);

		Instance target = ItemToInstance[targetItem];

		DropModeFlags = (int)DropModeFlagsEnum.Disabled;

		if (target == null)
			return;

		IDragDataUnion? dragData = DragData.Deserialize(data);

		if (dragData == null) return;

		List<TreeItem> draggedItems = [];

		switch (dragData)
    {
      case InstanceDragData instanceDrag:
      {
        foreach (Instance item in instanceDrag.Instances)
        {
          draggedItems.Add(InstanceToItem[item]);
        }

        break;
      }

      case FileDragData fileDrag:
      {
        Root.CreatorContext.Selections.DeselectAll();
        Root.PlayerGUI.GrabFocus();

        foreach (string file in fileDrag.Files)
        {
          Instance? instance = InsertFile(file, target);
          if (instance != null)
          {
            Root.CreatorContext.Selections.Select(instance);
          }
        }

        break;
      }

      default:
        return;
    }

		Instance? parentTo = null;
		int insertIndex = 0;

		switch (dropSection)
		{
			case -1: // Above Item
				parentTo = target.Parent;
				insertIndex = target.Index;
				break;
			case 0: // On Item
				parentTo = target;
				insertIndex = parentTo.GetChildren().Length; // Add at end
				break;
			case 1: // Below Item
				parentTo = target.Parent;
				insertIndex = target.Index + 1;

				// Check if target is the descendant of any dragged items
				bool isTargetParent = draggedItems
					.Select(item => ItemToInstance[item])
					.Where(inst => inst != null)
					.Any(inst => inst.Parent == target || inst.IsDescendantOf(target));

				if (isTargetParent)
				{
					// Moving to top of parent
					parentTo = target;
					insertIndex = 0;
				}
				break;
		}

		List<Instance> sortedDraggedInstances = [.. draggedItems
		.Select(item => ItemToInstance[item])
		.Where(inst => inst != null)
		.OrderBy(inst => inst.Index)];

		List<(Instance instance, Instance? oldParent, int oldIndex)> originalState = [];
		List<(Instance instance, Instance? newParent, int newIndex)> finalState = [];

		foreach (Instance draggedInstance in sortedDraggedInstances)
		{
			if (parentTo == null) continue;
			if (draggedInstance.IsAncestorOf(parentTo) || draggedInstance == parentTo)
				continue;

			try
			{
				Instance? oldParent = draggedInstance.Parent;
				int oldIndex = draggedInstance.Index;
				originalState.Add((draggedInstance, oldParent, oldIndex));

				// Calculate adjustment if moving within same parent
				int adjustedIndex = insertIndex;
				if (draggedInstance.Parent == parentTo && draggedInstance.Index < insertIndex)
				{
					// Item is being removed from before the target position
					adjustedIndex--;
				}

				finalState.Add((draggedInstance, parentTo, adjustedIndex));
			}
			catch (Exception ex)
			{
				PT.PrintErr(ex);
				CreatorService.Interface.PopupAlert(ex.Message);
				return;
			}
		}

		// Add history action
		if (originalState.Count <= 0) return;
		{
			history.NewAction($"Move {originalState.Count} instance(s)");

			history.AddDoCallback(new((_) =>
			{
				Root.CreatorContext.Selections.DeselectAll();
				foreach (var (instance, newParent, newIndex) in finalState)
				{
					if (newParent == null) continue;
					if (instance.Parent != newParent)
					{
						instance.Parent = newParent;
					}
					newParent.MoveChild(instance, newIndex);
					Root.CreatorContext.Selections.Select(instance);
				}
			}));

			history.AddUndoCallback(new((_) =>
			{
				Root.CreatorContext.Selections.DeselectAll();

				for (int i = originalState.Count - 1; i >= 0; i--)
				{
					var (instance, oldParent, oldIndex) = originalState[i];
					if (oldParent == null) continue;
					instance.Parent = oldParent;
					oldParent.MoveChild(instance, oldIndex);
					Root.CreatorContext.Selections.Select(instance);
				}
			}));

			history.CommitAction();
		}

	}

}
