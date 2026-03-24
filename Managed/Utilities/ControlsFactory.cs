using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArisenEditorFramework.Attributes;
using ReactiveUI;

namespace ArisenEditorFramework.Utilities;

public static class ControlsFactory
{
    public const string CustomMenuItem = "__custom_menu_items__";
    public static string[] InternalHeaderMenus = new string[]
    {
        "File",
        "Edit",
        "Content",
        "Entity",
        "Component",
        "GameObject", // Match design
        CustomMenuItem,
        "Window",
        "Help"
    };

    public static string[] InternalProjectMenus = new string[]
    {
        "Create",
        "Show in Explorer",
        "Open",
        "Delete",
        "Copy Path",
        CustomMenuItem,
    };
    
    public static string[] InternalHierarchyMenus = new string[]
    {
        "Cut",
        "Create Empty(with Transform)",
        "3D Object",
        "Light",
        "Camera",
        "UI",
        CustomMenuItem,
    };
    
    public enum MenuType
    {
        Project,
        Header,
        Hierarchy
    }

    public class MenuItemNode
    {
        public MethodInfo? MethodInfo;
        public List<string> children = new();
        public string Header = string.Empty;
        public int level;
        public bool separator;
        public string? Icon;
        public string? Shortcut;
        public int Priority;
    }

    private static Dictionary<(Assembly[], MenuType), Dictionary<string, MenuItemNode>> s_MenuCache = new(new AssemblyArrayComparer());

    private class AssemblyArrayComparer : IEqualityComparer<(Assembly[] Assemblies, MenuType Type)>
    {
        public bool Equals((Assembly[] Assemblies, MenuType Type) x, (Assembly[] Assemblies, MenuType Type) y)
        {
            if (x.Type != y.Type) return false;
            if (x.Assemblies.Length != y.Assemblies.Length) return false;
            for (int i = 0; i < x.Assemblies.Length; i++)
            {
                if (x.Assemblies[i] != y.Assemblies[i]) return false;
            }
            return true;
        }

        public int GetHashCode((Assembly[] Assemblies, MenuType Type) obj)
        {
            int hash = obj.Type.GetHashCode();
            foreach (var asm in obj.Assemblies)
            {
                hash ^= asm.GetHashCode();
            }
            return hash;
        }
    }

    private static void ParseItems(IEnumerable<Assembly> targetAssemblies, string[] internalMenus, MenuType menuType, out Dictionary<string, MenuItemNode> itemNodes)
    {
        var assemblies = targetAssemblies.ToArray();
        if (s_MenuCache.TryGetValue((assemblies, menuType), out var cachedNodes))
        {
            itemNodes = cachedNodes;
            return;
        }

        itemNodes = new Dictionary<string, MenuItemNode>();
        
        foreach (var targetAssembly in assemblies)
        {
            Type[] types = targetAssembly.GetTypes();
            
            foreach (var type in types)
            {
                MethodInfo[] methodInfos = type.GetMethods(
                    BindingFlags.Instance | 
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                    );
                foreach (var methodInfo in methodInfos)
                {
                    ArisenEditorFramework.Attributes.MenuItem? attribute = (ArisenEditorFramework.Attributes.MenuItem?)Attribute.GetCustomAttribute(methodInfo, typeof(ArisenEditorFramework.Attributes.MenuItem));
                    if (attribute != null)
                    {
                        var menuHierarchies = attribute.menuItem.Split(ArisenEditorFramework.Attributes.MenuItem.kMenuItemSeparators);
                        
                        if (menuHierarchies.Length <= 1)
                        {
                            continue;
                        }
                        
                        if (menuHierarchies[0] != menuType.ToString())
                        {
                            continue;
                        }
                        
                        MenuItemNode? parent = null;
                        string parentKey = "";
                        for (int i = 1; i < menuHierarchies.Length; ++i)
                        {
                            var childKey = menuHierarchies[i];
                            var key =  string.IsNullOrEmpty(parentKey) ? childKey : parentKey + ArisenEditorFramework.Attributes.MenuItem.kMenuItemSeparators + childKey;
                            parentKey = key;
                            if (itemNodes.TryGetValue(key, out var node))
                            {
                                parent = node;
                            }
                            else
                            {
                                if (parent != null)
                                {
                                    parent.children.Add(key);
                                }

                                bool isLeaf = (i == menuHierarchies.Length - 1);
                                parent = new MenuItemNode()
                                {
                                    Header = childKey,
                                    children = new List<string>(),
                                    // Only assign MethodInfo to leaf nodes to avoid intermediate nodes stealing callbacks.
                                    MethodInfo = isLeaf ? methodInfo : null,
                                    level = i,
                                    separator = attribute.separator,
                                    Icon = isLeaf ? attribute.icon : null,
                                    Shortcut = isLeaf ? attribute.shortcut : null,
                                    Priority = isLeaf ? attribute.priority : 0
                                };
                                itemNodes.Add(key, parent);
                            }
                        }
                    }
                }
            }
        }

        s_MenuCache[(assemblies, menuType)] = itemNodes;
    }

    private static void ParseUserMenuItems(string[] internalMenus, Dictionary<string, MenuItemNode> itemNodes, out List<MenuItemNode> userDefinedItems)
    {
        var values = itemNodes.Values.ToArray();
        userDefinedItems = new List<MenuItemNode>();
        foreach (var menuItemNode in values)
        {
            if (menuItemNode.level == 1 && Array.IndexOf(internalMenus, menuItemNode.Header) < 0)
            {
                userDefinedItems.Add(menuItemNode);
            }
        }
        
        userDefinedItems.Sort((a, b) => 
        {
            if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
            return string.Compare(a.Header, b.Header);
        });
    }

    public static List<ArisenEditorFramework.Core.Models.MenuItemModel> CreateMenuModel(IEnumerable<Assembly> targetAssemblies, MenuType menuType)
    {
        var rootItems = new List<ArisenEditorFramework.Core.Models.MenuItemModel>();
        ParseItems(targetAssemblies, InternalHeaderMenus, menuType, out var itemNodes);
        ParseUserMenuItems(InternalHeaderMenus, itemNodes, out var userDefinedItems);

        for (int i = 0; i < InternalHeaderMenus.Length; ++i)
        {
            var header = InternalHeaderMenus[i];
            if (header == CustomMenuItem)
            {
                foreach (var userNode in userDefinedItems)
                {
                    rootItems.Add(CreateItemModel(itemNodes, userNode));
                }
                continue;
            }

            if (itemNodes.TryGetValue(header, out var node))
            {
                var itemModel = CreateItemModel(itemNodes, node);
                rootItems.Add(itemModel);
            }
            else
            {
                // Ensure predefined menus always show even if empty
                rootItems.Add(new ArisenEditorFramework.Core.Models.MenuItemModel { Header = header });
            }
        }
        return rootItems;
    }

    private static ArisenEditorFramework.Core.Models.MenuItemModel CreateItemModel(Dictionary<string, MenuItemNode> itemNodes, MenuItemNode node)
    {
        var model = new ArisenEditorFramework.Core.Models.MenuItemModel
        {
            Header = node.Header,
            IsSeparator = node.separator,
            Icon = node.Icon,
            Shortcut = node.Shortcut,
            Priority = node.Priority
        };

        if (node.MethodInfo != null)
        {
            model.Command = ReactiveCommand.Create(() => 
            {
                if (node.MethodInfo.IsStatic)
                {
                    node.MethodInfo.Invoke(null, node.MethodInfo.GetParameters().Length == 0 ? null : new object[] { null, null });
                }
            });
        }

        foreach (var childKey in node.children)
        {
            if (itemNodes.TryGetValue(childKey, out var childNode))
            {
                model.Items.Add(CreateItemModel(itemNodes, childNode));
            }
        }

        model.Items.Sort((a, b) => 
        {
            if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
            return string.Compare(a.Header, b.Header);
        });

        return model;
    }
}
