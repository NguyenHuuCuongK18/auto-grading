-- Library database schema + seed data (SQL Server / SSMS compatible)
-- Lưu file này dưới tên: library_schema_and_seed_sqlserver.sql
-- Cách dùng: mở trong SQL Server Management Studio (SSMS) 20, tạo một query mới và chạy toàn bộ script.

-- Nếu database đã tồn tại, xóa an toàn
IF DB_ID(N'Library') IS NOT NULL
BEGIN
    ALTER DATABASE [Library] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [Library];
END
GO

-- Tạo database mới
CREATE DATABASE [Library];
GO

USE [Library];
GO

-- -----------------------------
-- Table: Genres
-- -----------------------------
CREATE TABLE dbo.Genres (
    GenreId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    GenreName NVARCHAR(100) NOT NULL
);
GO

-- -----------------------------
-- Table: Authors
-- -----------------------------
CREATE TABLE dbo.Authors (
    AuthorId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    BirthYear INT NULL
);
GO

-- -----------------------------
-- Table: Books
-- -----------------------------
CREATE TABLE dbo.Books (
    BookId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Title NVARCHAR(300) NOT NULL,
    PublicationYear INT NULL,
    GenreId INT NULL,
    CONSTRAINT FK_Books_Genres FOREIGN KEY (GenreId) REFERENCES dbo.Genres(GenreId) ON DELETE SET NULL
);
GO

-- -----------------------------
-- Table: BookAuthors (many-to-many)
-- -----------------------------
CREATE TABLE dbo.BookAuthors (
    BookId INT NOT NULL,
    AuthorId INT NOT NULL,
    CONSTRAINT PK_BookAuthors PRIMARY KEY (BookId, AuthorId),
    CONSTRAINT FK_BookAuthors_Books FOREIGN KEY (BookId) REFERENCES dbo.Books(BookId) ON DELETE CASCADE,
    CONSTRAINT FK_BookAuthors_Authors FOREIGN KEY (AuthorId) REFERENCES dbo.Authors(AuthorId) ON DELETE CASCADE
);
GO

-- -----------------------------
-- Table: BookCopies
-- -----------------------------
CREATE TABLE dbo.BookCopies (
    CopyId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    BookId INT NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT('available'),
    CONSTRAINT FK_BookCopies_Books FOREIGN KEY (BookId) REFERENCES dbo.Books(BookId) ON DELETE CASCADE,
    CONSTRAINT CK_BookCopies_Status CHECK (Status IN ('available','borrowed','lost','maintenance'))
);
GO

-- -----------------------------
-- Table: Borrowers
-- -----------------------------
CREATE TABLE dbo.Borrowers (
    BorrowerId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    Email NVARCHAR(200) NULL
);
GO

-- Create unique filtered index on Email so multiple NULLs allowed
CREATE UNIQUE INDEX UX_Borrowers_Email ON dbo.Borrowers(Email) WHERE Email IS NOT NULL;
GO

-- -----------------------------
-- Table: BorrowHistory
-- -----------------------------
CREATE TABLE dbo.BorrowHistory (
    BorrowId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CopyId INT NOT NULL,
    BorrowerId INT NOT NULL,
    BorrowDate DATE NOT NULL,
    ReturnDate DATE NULL,
    CONSTRAINT FK_BorrowHistory_Copy FOREIGN KEY (CopyId) REFERENCES dbo.BookCopies(CopyId) ON DELETE CASCADE,
    CONSTRAINT FK_BorrowHistory_Borrower FOREIGN KEY (BorrowerId) REFERENCES dbo.Borrowers(BorrowerId) ON DELETE CASCADE
);
GO

-- =============================
-- Seed data
-- =============================
SET NOCOUNT ON;

-- Genres (explicit IDs)
SET IDENTITY_INSERT dbo.Genres ON;
INSERT INTO dbo.Genres (GenreId, GenreName) VALUES
(1, N'Fantasy'),
(2, N'Dystopian'),
(3, N'Mystery'),
(4, N'Thriller'),
(5, N'Non-fiction'),
(6, N'Science Fiction');
SET IDENTITY_INSERT dbo.Genres OFF;
GO

-- Authors
SET IDENTITY_INSERT dbo.Authors ON;
INSERT INTO dbo.Authors (AuthorId, Name, BirthYear) VALUES
(1, N'J.K. Rowling', 1965),
(2, N'George Orwell', 1903),
(3, N'J.R.R. Tolkien', 1892),
(4, N'Agatha Christie', 1890),
(5, N'Dan Brown', 1964),
(6, N'Yuval Noah Harari', 1976),
(7, N'Margaret Atwood', 1939),
(8, N'Isaac Asimov', 1920);
SET IDENTITY_INSERT dbo.Authors OFF;
GO

-- Books
SET IDENTITY_INSERT dbo.Books ON;
INSERT INTO dbo.Books (BookId, Title, PublicationYear, GenreId) VALUES
(1, N'Harry Potter and the Philosopher''s Stone', 1997, 1),
(2, N'1984', 1949, 2),
(3, N'The Hobbit', 1937, 1),
(4, N'Murder on the Orient Express', 1934, 3),
(5, N'The Da Vinci Code', 2003, 4),
(6, N'Sapiens: A Brief History of Humankind', 2011, 5),
(7, N'The Handmaid''s Tale', 1985, 2),
(8, N'Foundation', 1951, 6),
(9, N'I, Robot', 1950, 6),
(10, N'Foundation and Empire', 1952, 6);
SET IDENTITY_INSERT dbo.Books OFF;
GO

-- BookAuthors (many-to-many)
INSERT INTO dbo.BookAuthors (BookId, AuthorId) VALUES
(1,1),
(2,2),
(3,3),
(4,4),
(5,5),
(6,6),
(7,7),
(8,8),
(9,8),
(10,8);
GO

-- BookCopies (explicit CopyId values)
SET IDENTITY_INSERT dbo.BookCopies ON;
INSERT INTO dbo.BookCopies (CopyId, BookId, Status) VALUES
(1,1,'available'),
(2,1,'borrowed'),
(3,1,'available'),
(4,2,'available'),
(5,2,'maintenance'),
(6,3,'available'),
(7,3,'borrowed'),
(8,4,'available'),
(9,4,'available'),
(10,5,'borrowed'),
(11,5,'available'),
(12,6,'available'),
(13,6,'available'),
(14,7,'borrowed'),
(15,7,'available'),
(16,8,'available'),
(17,8,'available'),
(18,9,'available'),
(19,9,'lost'),
(20,10,'available'),
(21,10,'borrowed');
SET IDENTITY_INSERT dbo.BookCopies OFF;
GO

-- Borrowers
SET IDENTITY_INSERT dbo.Borrowers ON;
INSERT INTO dbo.Borrowers (BorrowerId, Name, Email) VALUES
(1, N'Nguyen Van A', N'a.nguyen@example.com'),
(2, N'Tran Thi B', N'b.tran@example.com'),
(3, N'Le Van C', N'c.le@example.com'),
(4, N'Pham Thi D', N'd.pham@example.com'),
(5, N'Hoang Van E', N'e.hoang@example.com'),
(6, N'Vu Thi F', N'f.vu@example.com');
SET IDENTITY_INSERT dbo.Borrowers OFF;
GO

-- BorrowHistory
SET IDENTITY_INSERT dbo.BorrowHistory ON;
INSERT INTO dbo.BorrowHistory (BorrowId, CopyId, BorrowerId, BorrowDate, ReturnDate) VALUES
(1,2,1,'2025-08-10','2025-08-17'),
(2,7,2,'2025-08-12',NULL), -- currently borrowed
(3,10,3,'2025-08-01','2025-08-14'),
(4,14,4,'2025-08-20',NULL), -- currently borrowed
(5,21,5,'2025-08-22',NULL), -- currently borrowed
(6,11,6,'2025-07-05','2025-07-25'),
(7,1,2,'2025-06-01','2025-06-15');
SET IDENTITY_INSERT dbo.BorrowHistory OFF;
GO

-- Update statuses to match BorrowHistory where ReturnDate IS NULL
UPDATE bc
SET bc.Status = 'borrowed'
FROM dbo.BookCopies bc
INNER JOIN dbo.BorrowHistory bh ON bc.CopyId = bh.CopyId
WHERE bh.ReturnDate IS NULL;
GO

-- Finished
PRINT N'Seed complete. Database [Library] created with sample data.';
GO
