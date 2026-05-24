using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Sets up a SQLite FTS5 virtual table over CatalogTitles and keeps it in sync
/// via triggers. Called once per app boot after EF migrations.
/// FTS lets the catalog search 2M+ rows by title in single-digit ms.
/// </summary>
public static class CatalogFts
{
    public const string VirtualTable = "CatalogTitlesFts";

    public static void EnsureCreated(HarvesterDbContext db)
    {
        var sql = $@"
CREATE VIRTUAL TABLE IF NOT EXISTS {VirtualTable}
USING fts5(
    TitleName,
    OriginalTitle,
    content='CatalogTitles',
    content_rowid='Id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER IF NOT EXISTS CatalogTitles_ai AFTER INSERT ON CatalogTitles BEGIN
    INSERT INTO {VirtualTable}(rowid, TitleName, OriginalTitle)
    VALUES (new.Id, new.TitleName, COALESCE(new.OriginalTitle, ''));
END;

CREATE TRIGGER IF NOT EXISTS CatalogTitles_ad AFTER DELETE ON CatalogTitles BEGIN
    INSERT INTO {VirtualTable}({VirtualTable}, rowid, TitleName, OriginalTitle)
    VALUES ('delete', old.Id, old.TitleName, COALESCE(old.OriginalTitle, ''));
END;

CREATE TRIGGER IF NOT EXISTS CatalogTitles_au AFTER UPDATE ON CatalogTitles BEGIN
    INSERT INTO {VirtualTable}({VirtualTable}, rowid, TitleName, OriginalTitle)
    VALUES ('delete', old.Id, old.TitleName, COALESCE(old.OriginalTitle, ''));
    INSERT INTO {VirtualTable}(rowid, TitleName, OriginalTitle)
    VALUES (new.Id, new.TitleName, COALESCE(new.OriginalTitle, ''));
END;";

        db.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>
    /// Rebuilds the FTS index from scratch. Use after bulk-loading the catalog.
    /// </summary>
    public static void Rebuild(HarvesterDbContext db)
    {
        db.Database.ExecuteSqlRaw($@"
            INSERT INTO {VirtualTable}({VirtualTable}) VALUES('delete-all');
            INSERT INTO {VirtualTable}(rowid, TitleName, OriginalTitle)
            SELECT Id, TitleName, COALESCE(OriginalTitle, '') FROM CatalogTitles;");
    }
}
