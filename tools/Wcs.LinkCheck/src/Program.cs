// Wcs.LinkCheck: checks every Markdown link of a repository the way GitHub resolves it.
//
//   dotnet run --project tools/Wcs.LinkCheck/src -- [<root>] [--exclude <path>]... [--forbid <path>]...
//
// <root> defaults to the current directory. Every *.md under it is read, except the folders .git,
// bin, obj, node_modules and TestResults and the paths given with --exclude (relative to the root;
// docs/history and this tool's own test fixtures are always excluded). A path given with --forbid is
// a part that must not be linked at all (a part left out of the public copy).
//
// Exit code: 0 when every link resolves, 1 with the list of broken links, 2 on a usage error.

using Wcs.LinkCheck;

return LinkCheckCommand.Run(args, Console.Out, Console.Error);
