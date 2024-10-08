USE [OpenWorldServer]
GO
/****** Object:  StoredProcedure [dbo].[AddClass]    Script Date: 10/2/2023 5:30:15 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

ALTER PROCEDURE [dbo].[AddClass]
(
	@CustomerGUID uniqueidentifier,
	@ClassName varchar(50),
	@Gender tinyint,
	@Description varchar(max),
	@StartingMapName varchar(50),
	@X float,
	@Y float,
	@Z float,
	@RX float,
	@RY float,
	@RZ float,
	@TeamNumber int

)
AS
BEGIN
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE

	IF (NOT EXISTS (SELECT 1 FROM Class C WITH (HOLDLOCK) WHERE C.CustomerGUID=@CustomerGUID AND C.ClassName=@ClassName))
	BEGIN

		INSERT INTO Class(
			CustomerGUID, 
			ClassName,	
			Gender,
			[Description],
			StartingMapName,
			X,
			Y,
			Z,
			RX,
			RY,
			RZ,
			TeamNumber
		)
		VALUES (
			@CustomerGUID, 
			@ClassName,	
			@Gender,
			@Description,
			@StartingMapName,		
			@X,
			@Y,
			@Z,
			@RX,
			@RY,
			@RZ,
			@TeamNumber
			)

		DECLARE @ClassID int

		SELECT @ClassID=SCOPE_IDENTITY()

		SELECT @ClassID as ClassID

	END
	ELSE
	BEGIN

		SELECT -1 as ClassID

	END

END
