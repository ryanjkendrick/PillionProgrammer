using Pillion.Indexing.Roots;

namespace Pillion.Indexing.Tests;

/// <summary>Reading the checked-out ref and commit without git (work item 2.10): plain repositories, linked worktrees, packed refs.</summary>
public sealed class GitHeadTests
{
    private const string CommitA = "1111111111111111111111111111111111111111";
    private const string CommitB = "2222222222222222222222222222222222222222";

    [Fact]
    public void ABranch_ReadsAsTheRefAndItsCommit()
    {
        using var temp = new TempDirectory();
        temp.Write(".git/HEAD", "ref: refs/heads/main\n");
        temp.Write(".git/refs/heads/main", CommitA + "\n");

        Assert.Equal($"ref: refs/heads/main @ {CommitA}", GitHead.Read(temp.File(".git")));

        // A commit on the branch moves the ref, not HEAD: the reading changes.
        temp.Write(".git/refs/heads/main", CommitB + "\n");
        Assert.Equal($"ref: refs/heads/main @ {CommitB}", GitHead.Read(temp.File(".git")));

        // A branch switch.
        temp.Write(".git/HEAD", "ref: refs/heads/feature\n");
        temp.Write(".git/refs/heads/feature", CommitA + "\n");
        Assert.Equal($"ref: refs/heads/feature @ {CommitA}", GitHead.Read(temp.File(".git")));
    }

    [Fact]
    public void ADetachedHead_ReadsAsTheCommit()
    {
        using var temp = new TempDirectory();
        temp.Write(".git/HEAD", CommitA + "\n");

        Assert.Equal(CommitA, GitHead.Read(temp.File(".git")));
    }

    [Fact]
    public void APackedRef_IsResolved_AndAnUnbornBranchReadsAsTheRefAlone()
    {
        using var temp = new TempDirectory();
        temp.Write(".git/HEAD", "ref: refs/heads/release\n");
        temp.Write(".git/packed-refs", $"# pack-refs with: peeled fully-peeled sorted\n{CommitB} refs/heads/release\n^{CommitA}\n{CommitA} refs/tags/v1\n");

        Assert.Equal($"ref: refs/heads/release @ {CommitB}", GitHead.Read(temp.File(".git")));

        temp.Write(".git/HEAD", "ref: refs/heads/unborn\n");
        Assert.Equal("ref: refs/heads/unborn", GitHead.Read(temp.File(".git")));
    }

    [Fact]
    public void ALinkedWorktree_ResolvesItsBranchInTheCommonDirectory()
    {
        using var temp = new TempDirectory();
        temp.Write("main/.git/refs/heads/topic", CommitB + "\n");
        temp.Write("main/.git/worktrees/wt/HEAD", "ref: refs/heads/topic\n");
        temp.Write("main/.git/worktrees/wt/commondir", "../..\n");
        temp.Write("wt/.git", "gitdir: ../main/.git/worktrees/wt\n");

        var root = Assert.Single(RepositoryRoots.Resolve([temp.File("wt")]));
        Assert.Equal(RepositoryRootKind.GitWorktree, root.Kind);
        Assert.Equal(Path.GetFullPath(temp.File("main/.git")), GitHead.CommonDirectory(root.GitDirectory!));
        Assert.Equal($"ref: refs/heads/topic @ {CommitB}", GitHead.Read(root.GitDirectory!));
    }

    [Fact]
    public void AMissingHead_ReadsAsNull()
    {
        using var temp = new TempDirectory();
        temp.MakeDirectory(".git");

        Assert.Null(GitHead.Read(temp.File(".git")));
    }
}
