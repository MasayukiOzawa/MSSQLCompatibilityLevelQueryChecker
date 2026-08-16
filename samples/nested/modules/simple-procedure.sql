-- サブディレクトリも再帰的に解析されることを確認するサンプルです。
CREATE PROCEDURE [dbo].[usp_GetSampleValue]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 1 AS [SampleValue];
END;
