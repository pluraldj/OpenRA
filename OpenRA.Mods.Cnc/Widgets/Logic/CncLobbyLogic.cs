#region Copyright & License Information
/*
 * Copyright 2007-2011 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Network;
using OpenRA.Traits;
using OpenRA.Widgets;
using OpenRA.Mods.RA.Widgets.Logic;

namespace OpenRA.Mods.Cnc.Widgets.Logic
{
	public class CncLobbyLogic
	{
		Widget EditablePlayerTemplate, NonEditablePlayerTemplate, EmptySlotTemplate,
			   EditableSpectatorTemplate, NonEditableSpectatorTemplate, NewSpectatorTemplate;
		ScrollPanelWidget chatPanel;
		Widget chatTemplate;

		ScrollPanelWidget Players;
		Dictionary<string, string> CountryNames;
		string MapUid;
		Map Map;

		CncColorPickerPaletteModifier PlayerPalettePreview;

		readonly Action OnGameStart;
		readonly Action onExit;
		readonly OrderManager orderManager;

		// Listen for connection failures
		void ConnectionStateChanged(OrderManager om)
		{
			if (om.Connection.ConnectionState == ConnectionState.NotConnected)
			{
				// Show connection failed dialog
				CloseWindow();

				Action onConnect = () =>
				{
					Game.OpenWindow("SERVER_LOBBY", new WidgetArgs()
					{
						{ "onExit", onExit },
						{ "onStart", OnGameStart },
						{ "addBots", false }
					});
				};

				Action onRetry = () =>
				{
					CloseWindow();
					CncConnectingLogic.Connect(om.Host, om.Port, onConnect, onExit);
				};

				Widget.OpenWindow("CONNECTIONFAILED_PANEL", new WidgetArgs()
	            {
					{ "onAbort", onExit },
					{ "onRetry", onRetry },
					{ "host", om.Host },
					{ "port", om.Port }
				});
			}
		}

		public void CloseWindow()
		{
			Game.LobbyInfoChanged -= UpdateCurrentMap;
			Game.LobbyInfoChanged -= UpdatePlayerList;
			Game.BeforeGameStart -= OnGameStart;
			Game.AddChatLine -= AddChatLine;
			Game.ConnectionStateChanged -= ConnectionStateChanged;

			Widget.CloseWindow();
		}

		[ObjectCreator.UseCtor]
		internal CncLobbyLogic([ObjectCreator.Param( "widget" )] Widget lobby,
		                       [ObjectCreator.Param] World world, // Shellmap world
		                       [ObjectCreator.Param] OrderManager orderManager,
		                       [ObjectCreator.Param] Action onExit,
		                       [ObjectCreator.Param] Action onStart,
		                       [ObjectCreator.Param] bool addBots)
		{
			this.orderManager = orderManager;
			this.OnGameStart = () => { CloseWindow(); onStart(); };
			this.onExit = onExit;

			Game.LobbyInfoChanged += UpdateCurrentMap;
			Game.LobbyInfoChanged += UpdatePlayerList;
			Game.BeforeGameStart += OnGameStart;
			Game.AddChatLine += AddChatLine;
			Game.ConnectionStateChanged += ConnectionStateChanged;

			UpdateCurrentMap();
			PlayerPalettePreview = world.WorldActor.Trait<CncColorPickerPaletteModifier>();
			PlayerPalettePreview.Ramp = Game.Settings.Player.ColorRamp;
			Players = lobby.GetWidget<ScrollPanelWidget>("PLAYERS");
			EditablePlayerTemplate = Players.GetWidget("TEMPLATE_EDITABLE_PLAYER");
			NonEditablePlayerTemplate = Players.GetWidget("TEMPLATE_NONEDITABLE_PLAYER");
			EmptySlotTemplate = Players.GetWidget("TEMPLATE_EMPTY");
			EditableSpectatorTemplate = Players.GetWidget("TEMPLATE_EDITABLE_SPECTATOR");
			NonEditableSpectatorTemplate = Players.GetWidget("TEMPLATE_NONEDITABLE_SPECTATOR");
			NewSpectatorTemplate = Players.GetWidget("TEMPLATE_NEW_SPECTATOR");

			var mapPreview = lobby.GetWidget<MapPreviewWidget>("MAP_PREVIEW");
			mapPreview.IsVisible = () => Map != null;
			mapPreview.Map = () => Map;
			mapPreview.OnMouseDown = mi =>
			{
				var map = mapPreview.Map();
				if (map == null || mi.Button != MouseButton.Left
				    || orderManager.LocalClient.State == Session.ClientState.Ready)
					return;

				var p = map.SpawnPoints
					.Select((sp, i) => Pair.New(mapPreview.ConvertToPreview(map, sp), i))
					.Where(a => (a.First - mi.Location).LengthSquared < 64)
					.Select(a => a.Second + 1)
					.FirstOrDefault();

				var owned = orderManager.LobbyInfo.Clients.Any(c => c.SpawnPoint == p);
				if (p == 0 || !owned)
					orderManager.IssueOrder(Order.Command("spawn {0} {1}".F(orderManager.LocalClient.Index, p)));
			};

			var mapTitle = lobby.GetWidget<LabelWidget>("MAP_TITLE");
			mapTitle.IsVisible = () => Map != null;
			mapTitle.GetText = () => Map.Title;

			mapPreview.SpawnColors = () =>
			{
				var spawns = Map.SpawnPoints;
				var sc = new Dictionary<int2, Color>();

				for (int i = 1; i <= spawns.Count(); i++)
				{
					var client = orderManager.LobbyInfo.Clients.FirstOrDefault(c => c.SpawnPoint == i);
					if (client == null)
						continue;
					sc.Add(spawns.ElementAt(i - 1), client.ColorRamp.GetColor(0));
				}
				return sc;
			};

			CountryNames = Rules.Info["world"].Traits.WithInterface<CountryInfo>()
				.ToDictionary(a => a.Race, a => a.Name);
			CountryNames.Add("random", "Any");

			var mapButton = lobby.GetWidget<ButtonWidget>("CHANGEMAP_BUTTON");
			mapButton.OnClick = () =>
			{
				var onSelect = new Action<Map>(m =>
				{
					orderManager.IssueOrder(Order.Command("map " + m.Uid));
					Game.Settings.Server.Map = m.Uid;
					Game.Settings.Save();
				});

				Widget.OpenWindow("MAPCHOOSER_PANEL", new WidgetArgs()
				{
					{ "initialMap", Map.Uid },
					{ "onExit", () => {} },
					{ "onSelect", onSelect }
				});
			};
			mapButton.IsVisible = () => mapButton.Visible && Game.IsHost;

			var disconnectButton = lobby.GetWidget<ButtonWidget>("DISCONNECT_BUTTON");
			disconnectButton.OnClick = () => { CloseWindow(); onExit(); };

			var gameStarting = false;

			var allowCheats = lobby.GetWidget<CheckboxWidget>("ALLOWCHEATS_CHECKBOX");
			allowCheats.IsChecked = () => orderManager.LobbyInfo.GlobalSettings.AllowCheats;
			allowCheats.IsDisabled = () => !Game.IsHost || gameStarting || orderManager.LocalClient == null
				|| orderManager.LocalClient.State == Session.ClientState.Ready;
			allowCheats.OnClick = () =>	orderManager.IssueOrder(Order.Command(
						"allowcheats {0}".F(!orderManager.LobbyInfo.GlobalSettings.AllowCheats)));

			var startGameButton = lobby.GetWidget<ButtonWidget>("START_GAME_BUTTON");
			startGameButton.IsVisible = () => Game.IsHost;
			startGameButton.IsDisabled = () => gameStarting;
			startGameButton.OnClick = () =>
			{
				gameStarting = true;
				orderManager.IssueOrder(Order.Command("startgame"));
			};

			bool teamChat = false;
			var chatLabel = lobby.GetWidget<LabelWidget>("LABEL_CHATTYPE");
			var chatTextField = lobby.GetWidget<TextFieldWidget>("CHAT_TEXTFIELD");

			chatTextField.OnEnterKey = () =>
			{
				if (chatTextField.Text.Length == 0)
					return true;

				orderManager.IssueOrder(Order.Chat(teamChat, chatTextField.Text));
				chatTextField.Text = "";
				return true;
			};

			chatTextField.OnTabKey = () =>
			{
				teamChat ^= true;
				chatLabel.Text = (teamChat) ? "Team:" : "Chat:";
				return true;
			};

			chatPanel = lobby.GetWidget<ScrollPanelWidget>("CHAT_DISPLAY");
			chatTemplate = chatPanel.GetWidget("CHAT_TEMPLATE");
			chatPanel.RemoveChildren();

			lobby.GetWidget<ButtonWidget>("MUSIC_BUTTON").OnClick = () =>
			{
				Widget.OpenWindow("MUSIC_PANEL", new WidgetArgs()
                {
					{ "onExit", () => {} },
				});
			};

			// Add a bot on the first lobbyinfo update
			if (addBots)
				Game.LobbyInfoChanged += WidgetUtils.Once(() =>
				{
					var slot = orderManager.LobbyInfo.FirstEmptySlot();
					var bot = Rules.Info["player"].Traits.WithInterface<IBotInfo>().Select(t => t.Name).FirstOrDefault();
					if (slot != null && bot != null)
						orderManager.IssueOrder(Order.Command("slot_bot {0} {1}".F(slot, bot)));
				});
		}

		public void AddChatLine(Color c, string from, string text)
		{
			var template = chatTemplate.Clone();
			var nameLabel = template.GetWidget<LabelWidget>("NAME");
			var timeLabel = template.GetWidget<LabelWidget>("TIME");
			var textLabel = template.GetWidget<LabelWidget>("TEXT");

			var name = from+":";
			var font = Game.Renderer.Fonts[nameLabel.Font];
			var nameSize = font.Measure(from);

			var time = System.DateTime.Now;
			timeLabel.GetText = () => "{0:D2}:{1:D2}".F(time.Hour, time.Minute);

			nameLabel.GetColor = () => c;
			nameLabel.GetText = () => name;
			nameLabel.Bounds.Width = nameSize.X;
			textLabel.Bounds.X += nameSize.X;
			textLabel.Bounds.Width -= nameSize.X;

			// Hack around our hacky wordwrap behavior: need to resize the widget to fit the text
			text = WidgetUtils.WrapText(text, textLabel.Bounds.Width, font);
			textLabel.GetText = () => text;
			var oldHeight = textLabel.Bounds.Height;
			textLabel.Bounds.Height = font.Measure(text).Y;
			var dh = textLabel.Bounds.Height - oldHeight;
			if (dh > 0)
				template.Bounds.Height += dh;

			chatPanel.AddChild(template);
			chatPanel.ScrollToBottom();
			Sound.Play("scold1.aud");
		}

		void UpdateCurrentMap()
		{
			if (MapUid == orderManager.LobbyInfo.GlobalSettings.Map) return;
			MapUid = orderManager.LobbyInfo.GlobalSettings.Map;
			Map = new Map(Game.modData.AvailableMaps[MapUid].Path);

			var title = Widget.RootWidget.GetWidget<LabelWidget>("TITLE");
			title.Text = orderManager.LobbyInfo.GlobalSettings.ServerName;
		}

		void ShowSpawnDropDown(DropDownButtonWidget dropdown, Session.Client client)
		{
			Func<int, ScrollItemWidget, ScrollItemWidget> setupItem = (ii, itemTemplate) =>
			{
				var item = ScrollItemWidget.Setup(itemTemplate,
				                                  () => client.SpawnPoint == ii,
				                                  () => orderManager.IssueOrder(Order.Command("spawn {0} {1}".F(client.Index, ii))));
				item.GetWidget<LabelWidget>("LABEL").GetText = () => ii == 0 ? "-" : ii.ToString();
				return item;
			};

			var taken = orderManager.LobbyInfo.Clients
				.Where(c => c.SpawnPoint != 0 && c.SpawnPoint != client.SpawnPoint && c.Slot != null)
				.Select(c => c.SpawnPoint).ToList();

			var options = Graphics.Util.MakeArray(Map.SpawnPoints.Count() + 1, i => i).Except(taken).ToList();
			dropdown.ShowDropDown("TEAM_DROPDOWN_TEMPLATE", 150, options, setupItem);
		}

		void ShowColorDropDown(DropDownButtonWidget color, Session.Client client)
		{
			Action<ColorRamp> onSelect = c =>
			{
				if (client.Bot == null)
				{
					Game.Settings.Player.ColorRamp = c;
					Game.Settings.Save();
				}

				color.RemovePanel();
				orderManager.IssueOrder(Order.Command("color {0} {1}".F(client.Index, c)));
			};

			Action<ColorRamp> onChange = c => PlayerPalettePreview.Ramp = c;

			var colorChooser = Game.LoadWidget(orderManager.world, "COLOR_CHOOSER", null, new WidgetArgs()
			{
				{ "onSelect", onSelect },
				{ "onChange", onChange },
				{ "initialRamp", client.ColorRamp }
			});

			color.AttachPanel(colorChooser);
		}

		void UpdatePlayerList()
		{
			// This causes problems for people who are in the process of editing their names (the widgets vanish from beneath them)
			// Todo: handle this nicer
			Players.RemoveChildren();

			foreach (var kv in orderManager.LobbyInfo.Slots)
			{
				var key = kv.Key;
				var slot = kv.Value;
				var client = orderManager.LobbyInfo.ClientInSlot(key);
				Widget template;

				// Empty slot
				if (client == null)
				{
					template = EmptySlotTemplate.Clone();
					Func<string> getText = () => slot.Closed ? "Closed" : "Open";
					var ready = orderManager.LocalClient.State == Session.ClientState.Ready;

					if (Game.IsHost)
					{
						var name = template.GetWidget<DropDownButtonWidget>("NAME_HOST");
						name.IsVisible = () => true;
						name.IsDisabled = () => ready;
						name.GetText = getText;
						name.OnMouseDown = _ => LobbyUtils.ShowSlotDropDown(name, slot, client, orderManager);
					}
					else
					{
						var name = template.GetWidget<LabelWidget>("NAME");
						name.IsVisible = () => true;
						name.GetText = getText;
					}

					var join = template.GetWidget<ButtonWidget>("JOIN");
					join.IsVisible = () => !slot.Closed;
					join.IsDisabled = () => ready;
					join.OnClick = () => orderManager.IssueOrder(Order.Command("slot " + key));
				}
				// Editable player in slot
				else if ((client.Index == orderManager.LocalClient.Index) ||
				         (client.Bot != null && Game.IsHost))
				{
					template = EditablePlayerTemplate.Clone();
					var botReady = (client.Bot != null && Game.IsHost
						    && orderManager.LocalClient.State == Session.ClientState.Ready);
					var ready = botReady || client.State == Session.ClientState.Ready;

					if (client.Bot != null)
					{
						var name = template.GetWidget<DropDownButtonWidget>("BOT_DROPDOWN");
						name.IsVisible = () => true;
						name.IsDisabled = () => ready;
						name.GetText = () => client.Name;
						name.OnMouseDown = _ => LobbyUtils.ShowSlotDropDown(name, slot, client, orderManager);
					}
					else
					{
						var name = template.GetWidget<TextFieldWidget>("NAME");
						name.IsVisible = () => true;
						name.IsDisabled = () => ready;
						LobbyUtils.SetupNameWidget(orderManager, client, name);
					}

					var color = template.GetWidget<DropDownButtonWidget>("COLOR");
					color.IsDisabled = () => slot.LockColor || ready;
					color.OnMouseDown = _ => ShowColorDropDown(color, client);

					var colorBlock = color.GetWidget<ColorBlockWidget>("COLORBLOCK");
					colorBlock.GetColor = () => client.ColorRamp.GetColor(0);

					var faction = template.GetWidget<DropDownButtonWidget>("FACTION");
					faction.IsDisabled = () => slot.LockRace || ready;
					faction.OnMouseDown = _ => LobbyUtils.ShowRaceDropDown(faction, client, orderManager, CountryNames);

					var factionname = faction.GetWidget<LabelWidget>("FACTIONNAME");
					factionname.GetText = () => CountryNames[client.Country];
					var factionflag = faction.GetWidget<ImageWidget>("FACTIONFLAG");
					factionflag.GetImageName = () => client.Country;
					factionflag.GetImageCollection = () => "flags";

					var team = template.GetWidget<DropDownButtonWidget>("TEAM");
					team.IsDisabled = () => slot.LockTeam || ready;
					team.OnMouseDown = _ => LobbyUtils.ShowTeamDropDown(team, client, orderManager, Map);
					team.GetText = () => (client.Team == 0) ? "-" : client.Team.ToString();

					var spawn = template.GetWidget<DropDownButtonWidget>("SPAWN");
					spawn.IsDisabled = () => slot.LockSpawn || ready;
					spawn.OnMouseDown = _ => ShowSpawnDropDown(spawn, client);
					spawn.GetText = () => (client.SpawnPoint == 0) ? "-" : client.SpawnPoint.ToString();

					if (client.Bot == null)
					{
						// local player
						var status = template.GetWidget<CheckboxWidget>("STATUS_CHECKBOX");
						status.IsChecked = () => ready;
						status.IsVisible = () => true;
						status.OnClick += CycleReady;
					}
					else // Bot
						template.GetWidget<ImageWidget>("STATUS_IMAGE").IsVisible = () => true;
				}
				// Non-editable player in slot
				else
				{
					template = NonEditablePlayerTemplate.Clone();
					template.GetWidget<LabelWidget>("NAME").GetText = () => client.Name;
					var color = template.GetWidget<ColorBlockWidget>("COLOR");
					color.GetColor = () => client.ColorRamp.GetColor(0);

					var faction = template.GetWidget<LabelWidget>("FACTION");
					var factionname = faction.GetWidget<LabelWidget>("FACTIONNAME");
					factionname.GetText = () => CountryNames[client.Country];
					var factionflag = faction.GetWidget<ImageWidget>("FACTIONFLAG");
					factionflag.GetImageName = () => client.Country;
					factionflag.GetImageCollection = () => "flags";

					var team = template.GetWidget<LabelWidget>("TEAM");
					team.GetText = () => (client.Team == 0) ? "-" : client.Team.ToString();

					var spawn = template.GetWidget<LabelWidget>("SPAWN");
					spawn.GetText = () => (client.SpawnPoint == 0) ? "-" : client.SpawnPoint.ToString();

					template.GetWidget<ImageWidget>("STATUS_IMAGE").IsVisible = () =>
						client.Bot != null || client.State == Session.ClientState.Ready;

					var kickButton = template.GetWidget<ButtonWidget>("KICK");
					kickButton.IsVisible = () => Game.IsHost && client.Index != orderManager.LocalClient.Index;
					kickButton.IsDisabled = () => orderManager.LocalClient.State == Session.ClientState.Ready;
					kickButton.OnClick = () => orderManager.IssueOrder(Order.Command("kick " + client.Index));
				}

				template.IsVisible = () => true;
				Players.AddChild(template);
			}

			// Add spectators
			foreach (var client in orderManager.LobbyInfo.Clients.Where(client => client.Slot == null))
			{
				Widget template;
				var c = client;
				var ready = c.State == Session.ClientState.Ready;
				// Editable spectator
				if (c.Index == orderManager.LocalClient.Index)
				{
					template = EditableSpectatorTemplate.Clone();
					var name = template.GetWidget<TextFieldWidget>("NAME");
					name.IsDisabled = () => ready;
					LobbyUtils.SetupNameWidget(orderManager, c, name);

					var color = template.GetWidget<DropDownButtonWidget>("COLOR");
					color.IsDisabled = () => ready;
					color.OnMouseDown = _ => ShowColorDropDown(color, c);

					var colorBlock = color.GetWidget<ColorBlockWidget>("COLORBLOCK");
					colorBlock.GetColor = () => c.ColorRamp.GetColor(0);

					var status = template.GetWidget<CheckboxWidget>("STATUS_CHECKBOX");
					status.IsChecked = () => ready;
					status.OnClick += CycleReady;
				}
				// Non-editable spectator
				else
				{
					template = NonEditableSpectatorTemplate.Clone();
					template.GetWidget<LabelWidget>("NAME").GetText = () => c.Name;
					var color = template.GetWidget<ColorBlockWidget>("COLOR");
					color.GetColor = () => c.ColorRamp.GetColor(0);

					template.GetWidget<ImageWidget>("STATUS_IMAGE").IsVisible = () =>
						c.Bot != null || c.State == Session.ClientState.Ready;

					var kickButton = template.GetWidget<ButtonWidget>("KICK");
					kickButton.IsVisible = () => Game.IsHost && c.Index != orderManager.LocalClient.Index;
					kickButton.IsDisabled = () => orderManager.LocalClient.State == Session.ClientState.Ready;
					kickButton.OnClick = () => orderManager.IssueOrder(Order.Command("kick " + c.Index));
				}

				template.IsVisible = () => true;
				Players.AddChild(template);
			}

			// Spectate button
			if (orderManager.LocalClient.Slot != null)
			{
				var spec = NewSpectatorTemplate.Clone();
				var btn = spec.GetWidget<ButtonWidget>("SPECTATE");
				btn.OnClick = () => orderManager.IssueOrder(Order.Command("spectate"));
				btn.IsDisabled = () => orderManager.LocalClient.State == Session.ClientState.Ready;
				spec.IsVisible = () => true;
				Players.AddChild(spec);
			}
		}

		bool SpawnPointAvailable(int index) { return (index == 0) || orderManager.LobbyInfo.Clients.All(c => c.SpawnPoint != index); }

		void CycleReady()
		{
			orderManager.IssueOrder(Order.Command("ready"));
		}
	}
}
